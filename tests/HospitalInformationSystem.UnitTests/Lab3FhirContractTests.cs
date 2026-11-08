using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab3FhirContractTests
{
    private const string FhirBase = "https://his-api:5201/fhir";

    [Theory]
    [InlineData("patient.json", "Patient")]
    [InlineData("practitioner.json", "Practitioner")]
    [InlineData("service-request-active.json", "ServiceRequest")]
    [InlineData("service-request-completed.json", "ServiceRequest")]
    [InlineData("observation-create.json", "Observation")]
    [InlineData("observation-final.json", "Observation")]
    [InlineData("service-requests-page-1.json", "Bundle")]
    [InlineData("service-requests-page-2.json", "Bundle")]
    [InlineData("service-requests-empty.json", "Bundle")]
    [InlineData("observations-empty.json", "Bundle")]
    [InlineData("observations-found.json", "Bundle")]
    [InlineData("operation-outcome-400.json", "OperationOutcome")]
    [InlineData("operation-outcome-401.json", "OperationOutcome")]
    [InlineData("operation-outcome-403.json", "OperationOutcome")]
    [InlineData("operation-outcome-404.json", "OperationOutcome")]
    [InlineData("operation-outcome-409.json", "OperationOutcome")]
    [InlineData("operation-outcome-500.json", "OperationOutcome")]
    public void MedicalFixturesConformToTheR5ObjectModel(string file, string resourceType)
    {
        ModelInfo.Version.Should().Be("5.0.0");
        Parse(ReadFixture(file)).TypeName.Should().Be(resourceType);
    }

    [Fact]
    public void PatientAndHistoricalRequesterRemainResolvableFromTheOrder()
    {
        var patient = Fixture<Patient>("patient.json");
        var requester = Fixture<Practitioner>("practitioner.json");
        var order = Fixture<ServiceRequest>("service-request-active.json");

        patient.Active.Should().BeTrue();
        patient.Name.Should().ContainSingle();
        patient.Name[0].Family.Should().Be("Петров");
        patient.Name[0].Given.Should().Equal("Алексей", "Андреевич");
        patient.BirthDate.Should().Be("1985-03-12");
        patient.Gender?.ToString().Should().Be("Male");
        requester.Active.Should().BeFalse();
        requester.Name[0].Family.Should().Be("Иванов");
        requester.Name[0].Given.Should().Equal("Иван", "Иванович");
        Assert.IsType<ResourceReference>(order.Subject).Reference.Should().Be(Reference("Patient", patient.Id));
        Assert.IsType<ResourceReference>(order.Requester).Reference.Should().Be(Reference("Practitioner", requester.Id));
    }

    [Fact]
    public void CompletedOrderPreservesOriginalIdentityAndClinicalData()
    {
        var active = Fixture<ServiceRequest>("service-request-active.json");
        var completed = Fixture<ServiceRequest>("service-request-completed.json");

        active.Status?.ToString().Should().Be("Active");
        completed.Status?.ToString().Should().Be("Completed");
        active.Intent?.ToString().Should().Be("Order");
        var code = RequestCode(active);
        code.Coding.Should().ContainSingle();
        code.Coding[0].System.Should().Be("http://loinc.org");
        code.Coding[0].Code.Should().Be("2345-7");
        active.AuthoredOn.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$");

        var expected = (ServiceRequest)active.DeepCopy();
        expected.Status = completed.Status;
        completed.IsExactly(expected).Should().BeTrue();
    }

    [Fact]
    public void ResultCreationAndReadFixturesDescribeOneResultForTheCorrectOrder()
    {
        var order = Fixture<ServiceRequest>("service-request-active.json");
        var create = Fixture<Observation>("observation-create.json");
        var saved = Fixture<Observation>("observation-final.json");
        var found = Fixture<Bundle>("observations-found.json");

        create.Id.Should().BeNull();
        saved.Id.Should().NotBe(order.Id);
        saved.Status?.ToString().Should().Be("Final");
        Assert.IsType<ResourceReference>(saved.Subject).Reference.Should().Be(Assert.IsType<ResourceReference>(order.Subject).Reference);
        saved.BasedOn.Should().ContainSingle();
        saved.BasedOn[0].Reference.Should().Be(Reference("ServiceRequest", order.Id));
        var code = Assert.IsType<CodeableConcept>(saved.Code);
        code.Coding.Should().ContainSingle();
        code.Coding[0].IsExactly(RequestCode(order).Coding[0]).Should().BeTrue();
        saved.Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("5.6");
        saved.Issued.Should().NotBeNull();
        saved.Issued!.Value.Offset.Should().Be(TimeSpan.Zero);
        saved.Issued.Value.Should().Be(DateTimeOffset.Parse("2026-11-05T10:05:00Z"));

        var expected = (Observation)create.DeepCopy();
        expected.Id = saved.Id;
        saved.IsExactly(expected).Should().BeTrue();
        found.Entry.Should().ContainSingle();
        found.Entry[0].FullUrl.Should().Be($"{FhirBase}/{Reference("Observation", saved.Id)}");
        found.Entry[0].Resource.Should().BeOfType<Observation>().Which.IsExactly(saved).Should().BeTrue();
    }

    [Fact]
    public void SearchPagesHaveAnOpaqueNextLinkAndDistinctResolvableOrders()
    {
        var first = Fixture<Bundle>("service-requests-page-1.json");
        var second = Fixture<Bundle>("service-requests-page-2.json");
        var patient = Fixture<Patient>("patient.json");
        var requester = Fixture<Practitioner>("practitioner.json");

        first.Type?.ToString().Should().Be("Searchset");
        second.Type.Should().Be(first.Type);
        first.Link.Single(x => x.Relation == "next").Url.Should().Be(second.Link.Single(x => x.Relation == "self").Url);
        second.Link.Should().NotContain(x => x.Relation == "next");
        first.Total.Should().Be(2);
        second.Total.Should().Be(first.Total);

        var entries = first.Entry.Concat(second.Entry).ToArray();
        entries.Should().HaveCount(2);
        entries.Select(x => Assert.IsAssignableFrom<Resource>(x.Resource).Id).Should().OnlyHaveUniqueItems();
        foreach (var entry in entries)
        {
            var order = entry.Resource.Should().BeOfType<ServiceRequest>().Which;
            entry.FullUrl.Should().Be($"{FhirBase}/{Reference("ServiceRequest", order.Id)}");
            Assert.IsType<Bundle.SearchComponent>(entry.Search).Mode?.ToString().Should().Be("Match");
            Assert.IsType<ResourceReference>(order.Subject).Reference.Should().Be(Reference("Patient", patient.Id));
            Assert.IsType<ResourceReference>(order.Requester).Reference.Should().Be(Reference("Practitioner", requester.Id));
        }
    }

    [Theory]
    [InlineData("service-requests-empty.json")]
    [InlineData("observations-empty.json")]
    public void EmptySearchIsASuccessfulSearchsetWithoutEntries(string file)
    {
        var search = Fixture<Bundle>(file);
        search.Type?.ToString().Should().Be("Searchset");
        search.Total.Should().Be(0);
        search.Entry.Should().BeEmpty();
        search.Link.Should().ContainSingle().Which.Relation.Should().Be("self");
        JsonNode.Parse(ReadFixture(file))!["entry"].Should().BeNull();
    }

    [Theory]
    [InlineData(400, "Invalid", "invalid_resource")]
    [InlineData(401, "Login", "unauthorized")]
    [InlineData(403, "Forbidden", "forbidden")]
    [InlineData(404, "NotFound", "not_found")]
    [InlineData(409, "Conflict", "result_already_exists")]
    [InlineData(500, "Exception", "internal_error")]
    public void FhirErrorsCarryStandardAndMachineReadableIssueCodes(int status, string issueCode, string machineCode)
    {
        var outcome = Fixture<OperationOutcome>($"operation-outcome-{status}.json");
        var issue = outcome.Issue.Should().ContainSingle().Which;
        issue.Severity?.ToString().Should().Be("Error");
        issue.Code?.ToString().Should().Be(issueCode);
        var details = Assert.IsType<CodeableConcept>(issue.Details);
        details.Coding.Should().ContainSingle();
        details.Coding[0].System.Should().Be("urn:medsoft:lab3:error");
        details.Coding[0].Code.Should().Be(machineCode);
        issue.Diagnostics.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void R4ServiceRequestCodeIsNotAnR5CodeableReference()
    {
        var json = JsonNode.Parse(ReadFixture("service-request-active.json"))!;
        json["code"] = json["code"]!["concept"]!.DeepClone();

        Action parse = () => Parse(json.ToJsonString());

        parse.Should().Throw<DeserializationFailedException>();
    }

    [Fact]
    public void R5DeserializerRejectsInvalidFhirPrimitiveTypes()
    {
        var json = JsonNode.Parse(ReadFixture("patient.json"))!;
        json["active"] = "yes";

        Action parse = () => Parse(json.ToJsonString());

        parse.Should().Throw<DeserializationFailedException>();
    }

    private static Resource Parse(string json) =>
        new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict)).DeserializeResource(json);

    private static T Fixture<T>(string file) where T : Resource => Parse(ReadFixture(file)).Should().BeOfType<T>().Which;

    private static string ReadFixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab3", file));

    private static CodeableConcept RequestCode(ServiceRequest request) =>
        Assert.IsType<CodeableConcept>(Assert.IsType<CodeableReference>(request.Code).Concept);

    private static string Reference(string resourceType, string? id)
    {
        Guid.TryParseExact(id, "D", out var parsed).Should().BeTrue();
        parsed.Should().NotBe(Guid.Empty);
        id.Should().Be(parsed.ToString("D"));
        return $"{resourceType}/{id}";
    }
}
