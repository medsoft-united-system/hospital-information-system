using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Api.Fhir;
using HospitalInformationSystem.Application.Fhir;
using DomainPatient = HospitalInformationSystem.Domain.Patients.Patient;
using DomainSex = HospitalInformationSystem.Domain.Patients.AdministrativeSex;
using DomainWorker = HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorker;
using DomainStatus = HospitalInformationSystem.Domain.MedicalRecords.LaboratoryAnalysisRequestStatus;

namespace HospitalInformationSystem.UnitTests;

public sealed class FhirResourceMappingTests
{
    private static readonly Guid PatientId = Guid.Parse("22222222-abcd-4222-8222-222222222222");
    private static readonly Guid WorkerId = Guid.Parse("33333333-abcd-4333-8333-333333333333");
    private static readonly Guid RequestId = Guid.Parse("11111111-abcd-4111-8111-111111111111");
    private static readonly Guid ResultId = Guid.Parse("44444444-abcd-4444-8444-444444444444");
    private static readonly DateTimeOffset At = new(2026, 11, 9, 13, 11, 12, 123, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(DomainSex.Male, "male")]
    [InlineData(DomainSex.Female, "female")]
    [InlineData(DomainSex.Unknown, "unknown")]
    public void Patient_ShouldExposeOnlyClinicalIdentityAndExplicitFhirGender(DomainSex sex, string expectedGender)
    {
        var patient = new DomainPatient
        {
            Id = PatientId, FirstName = "Анна", LastName = "Петрова", MiddleName = "Игоревна",
            BirthDate = new DateOnly(1990, 2, 3), Sex = sex, Comment = "Не передавать в интеграцию"
        };

        var json = Json(FhirResourceMapper.ToPatient(patient));

        json["resourceType"]!.GetValue<string>().Should().Be("Patient");
        json["id"]!.GetValue<string>().Should().Be(PatientId.ToString("D"));
        json["active"]!.GetValue<bool>().Should().BeTrue();
        json["name"]![0]!["use"]!.GetValue<string>().Should().Be("official");
        json["name"]![0]!["family"]!.GetValue<string>().Should().Be("Петрова");
        json["name"]![0]!["given"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("Анна", "Игоревна");
        json["birthDate"]!.GetValue<string>().Should().Be("1990-02-03");
        json["gender"]!.GetValue<string>().Should().Be(expectedGender);
        json.AsObject().Select(x => x.Key).Should().BeEquivalentTo(
            ["resourceType", "id", "active", "name", "birthDate", "gender"]);
        json.ToJsonString().Should().NotContain(patient.Comment!);
        Parse(json).Should().BeOfType<Patient>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Patient_WithoutMiddleName_ShouldNotInventAnEmptyGivenName(string? middle)
    {
        var patient = new DomainPatient
        {
            Id = PatientId, FirstName = "Иван", LastName = "Петров", MiddleName = middle,
            BirthDate = new DateOnly(1990, 1, 1)
        };

        FhirResourceMapper.ToPatient(patient).Name.Single().Given.Should().Equal("Иван");
    }

    [Fact]
    public void HistoricalPractitioner_ShouldRemainResolvableWithoutIdentityOrRoleFields()
    {
        var doctor = new DomainWorker
        {
            Id = WorkerId, FirstName = "Алексей", LastName = "Иванов", Position = "Врач-терапевт", IsDeleted = true
        };

        var json = Json(FhirResourceMapper.ToPractitioner(doctor));

        json["active"]!.GetValue<bool>().Should().BeFalse();
        json["id"]!.GetValue<string>().Should().Be(WorkerId.ToString("D"));
        json["name"]![0]!["given"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>().Should().Be("Алексей");
        json["qualification"]![0]!["code"]!["text"]!.GetValue<string>().Should().Be("Врач-терапевт");
        json.AsObject().Select(x => x.Key).Should().BeEquivalentTo(
            ["resourceType", "id", "active", "name", "qualification"]);
        Parse(json).Should().BeOfType<Practitioner>();
    }

    [Theory]
    [InlineData(DomainStatus.Active, "active")]
    [InlineData(DomainStatus.Completed, "completed")]
    public void ServiceRequest_ShouldUseR5CodeableReferenceAndSavedCatalogData(DomainStatus status, string wireStatus)
    {
        var request = Order(status) with { AuthoredAtUtc = At.AddTicks(4567), Comment = "Натощак" };

        var json = Json(FhirResourceMapper.ToServiceRequest(request));

        json["status"]!.GetValue<string>().Should().Be(wireStatus);
        json["intent"]!.GetValue<string>().Should().Be("order");
        json["code"]!["coding"].Should().BeNull();
        var coding = json["code"]!["concept"]!["coding"]!.AsArray().Should().ContainSingle().Which!;
        coding["system"]!.GetValue<string>().Should().Be("http://loinc.org");
        coding["code"]!.GetValue<string>().Should().Be("2345-7");
        coding["display"]!.GetValue<string>().Should().Be("Сохранённое название анализа");
        json["subject"]!["reference"]!.GetValue<string>().Should().Be($"Patient/{PatientId:D}");
        json["requester"]!["reference"]!.GetValue<string>().Should().Be($"Practitioner/{WorkerId:D}");
        json["authoredOn"]!.GetValue<string>().Should().Be("2026-11-09T10:11:12.123Z");
        json["note"]!.AsArray().Should().ContainSingle().Which!["text"]!.GetValue<string>().Should().Be("Натощак");
        request.AuthoredAtUtc.Should().Be(At.AddTicks(4567));
        Parse(json).Should().BeOfType<ServiceRequest>();
    }

    [Fact]
    public void ServiceRequest_WithoutComment_ShouldOmitNote()
    {
        Json(FhirResourceMapper.ToServiceRequest(Order())).AsObject().Should().NotContainKey("note");
    }

    [Theory]
    [InlineData("5.6")]
    [InlineData("0")]
    [InlineData("Отрицательно 🧪")]
    public void Observation_ShouldKeepStringValueAndDistinctResultIdentity(string value)
    {
        var source = new FhirObservationReadModel(ResultId, RequestId, PatientId, "2345-7",
            "Сохранённое название анализа", value, At.AddTicks(4567));

        var resource = FhirResourceMapper.ToObservation(source);
        var json = Json(resource);

        json["id"]!.GetValue<string>().Should().Be(ResultId.ToString("D")).And.NotBe(RequestId.ToString("D"));
        json["status"]!.GetValue<string>().Should().Be("final");
        json["basedOn"]!.AsArray().Should().ContainSingle().Which!["reference"]!.GetValue<string>()
            .Should().Be($"ServiceRequest/{RequestId:D}");
        json["subject"]!["reference"]!.GetValue<string>().Should().Be($"Patient/{PatientId:D}");
        json["code"]!["coding"]!.AsArray().Should().ContainSingle();
        json["code"]!["concept"].Should().BeNull();
        json["valueString"]!.GetValue<string>().Should().Be(value);
        json["issued"]!.GetValue<string>().Should().Be("2026-11-09T10:11:12.123Z");
        resource.IssuedElement!.Value.Should().Be(At.ToUniversalTime());
        json.AsObject().Select(x => x.Key).Should().NotContain(["valueQuantity", "valueInteger", "component", "effectiveDateTime"]);
        source.IssuedAtUtc.Should().Be(At.AddTicks(4567));
        Parse(json).Should().BeOfType<Observation>();
    }

    [Fact]
    public void Metadata_ShouldAdvertiseReadSearchAndOnlyObservationCreate()
    {
        var metadata = Json(FhirResourceMapper.CapabilityStatement());

        metadata["fhirVersion"]!.GetValue<string>().Should().Be("5.0.0");
        metadata["kind"]!.GetValue<string>().Should().Be("capability");
        metadata["software"]!["name"]!.GetValue<string>().Should().Be("MedSoft HIS");
        metadata["format"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>()
            .Should().Be("application/fhir+json");
        var resources = metadata["rest"]![0]!["resource"]!.AsArray();
        resources.Select(x => x!["type"]!.GetValue<string>()).Should()
            .BeEquivalentTo(["Patient", "Practitioner", "ServiceRequest", "Observation"]);
        foreach (var resource in resources)
        {
            var interactions = resource!["interaction"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()).ToArray();
            interactions.Should().Contain("read").And.NotContain(["update", "patch", "delete", "vread"]);
            if (resource["type"]!.GetValue<string>() == "Observation")
                interactions.Should().Contain("create");
            else interactions.Should().NotContain("create");
            if (resource["type"]!.GetValue<string>() is "ServiceRequest" or "Observation")
                interactions.Should().Contain("search-type");
            else interactions.Should().Equal("read");
        }
        Parse(metadata).Should().BeOfType<CapabilityStatement>();
    }

    [Fact]
    public void SearchBundle_ShouldKeepLinksAndFullUrlsUnderConfiguredPrefixedEndpoint()
    {
        const string self = "https://his-api:5201/prefix/fhir/ServiceRequest?status=active&_count=1";
        const string next = self + "&_cursor=opaque-token";
        var json = Json(FhirResourceMapper.SearchBundle("ServiceRequest",
            [FhirResourceMapper.ToServiceRequest(Order())], self, next));

        json["type"]!.GetValue<string>().Should().Be("searchset");
        json["link"]!.AsArray().Select(x => x!["relation"]!.GetValue<string>()).Should().Equal("self", "next");
        json["link"]![0]!["url"]!.GetValue<string>().Should().Be(self);
        json["link"]![1]!["url"]!.GetValue<string>().Should().Be(next);
        json["entry"]![0]!["fullUrl"]!.GetValue<string>().Should()
            .Be($"https://his-api:5201/prefix/fhir/ServiceRequest/{RequestId:D}");
        json["entry"]![0]!["search"]!["mode"]!.GetValue<string>().Should().Be("match");
        Parse(json).Should().BeOfType<Bundle>();
    }

    [Fact]
    public void EmptySearchBundle_ShouldReturnZeroWithoutEntryOrNext()
    {
        var json = Json(FhirResourceMapper.SearchBundle("Observation", [],
            $"https://his-api:5201/fhir/Observation?based-on=ServiceRequest%2F{RequestId:D}", null));

        json["total"]!.GetValue<int>().Should().Be(0);
        json.AsObject().Should().NotContainKey("entry");
        json["link"]!.AsArray().Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://elsewhere.test/fhir/ServiceRequest?_cursor=x")]
    [InlineData("http://his-api:5201/fhir/ServiceRequest?_cursor=x")]
    [InlineData("https://his-api:5201/fhir/Observation?_cursor=x")]
    public void SearchBundle_ShouldRejectForeignNextLinks(string next)
    {
        var map = () => FhirResourceMapper.SearchBundle("ServiceRequest", [],
            "https://his-api:5201/fhir/ServiceRequest", next);

        map.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SearchBundle_ShouldRejectUnencryptedSelfLink()
    {
        var map = () => FhirResourceMapper.SearchBundle("ServiceRequest", [],
            "http://his-api:5201/fhir/ServiceRequest", null);

        map.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(400, "invalid_search", "invalid")]
    [InlineData(401, "unauthorized", "login")]
    [InlineData(403, "forbidden", "forbidden")]
    [InlineData(404, "not_found", "not-found")]
    [InlineData(405, "not_supported", "not-supported")]
    [InlineData(406, "unsupported_media_type", "not-supported")]
    [InlineData(409, "conflict", "conflict")]
    [InlineData(413, "request_too_large", "too-costly")]
    [InlineData(415, "unsupported_media_type", "not-supported")]
    [InlineData(429, "rate_limited", "throttled")]
    [InlineData(500, "internal_error", "exception")]
    public void OperationOutcome_ShouldUseFhirIssueCodeAndC2MachineCode(int status, string machineCode, string issueCode)
    {
        var json = Json(FhirResourceMapper.OperationOutcome(status, machineCode, "Безопасное сообщение", "trace:42"));

        json["issue"]![0]!["severity"]!.GetValue<string>().Should().Be("error");
        json["issue"]![0]!["code"]!.GetValue<string>().Should().Be(issueCode);
        json["issue"]![0]!["details"]!["coding"]![0]!["system"]!.GetValue<string>().Should()
            .Be("urn:medsoft:lab3:error");
        json["issue"]![0]!["details"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(machineCode);
        json["issue"]![0]!["diagnostics"]!.GetValue<string>().Should().Be("Безопасное сообщение");
        json["extension"]![0]!["valueString"]!.GetValue<string>().Should().Be("trace:42");
        Parse(json).Should().BeOfType<OperationOutcome>();
    }

    private static FhirServiceRequestReadModel Order(DomainStatus status = DomainStatus.Active) =>
        new(RequestId, PatientId, WorkerId, "2345-7", "Сохранённое название анализа", null, At, status);
    private static JsonNode Json(Resource resource) => JsonNode.Parse(FhirJsonSerializer.Default.SerializeToString(resource))!;
    private static Resource Parse(JsonNode json) => new FhirJsonDeserializer(
        new DeserializerSettings().UsingMode(DeserializationMode.Strict)).DeserializeResource(json.ToJsonString());
}
