using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.Patients;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7ContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void SharedProtocolConstantsPinTheC4V1Profile()
    {
        Hl7ImportProtocol.ContractVersion.Should().Be("C4-v1");
        Hl7ImportProtocol.Version.Should().Be("2.8");
        Hl7ImportProtocol.Role.Should().Be("HL7Import");
        Hl7ImportProtocol.SourceSystem.Should().Be("LAB4_XML");
        Hl7ImportProtocol.DefaultSourceTimeZone.Should().Be("Europe/Moscow");
        Hl7ImportProtocol.DefaultMllpPort.Should().Be(2575);
        Hl7ImportProtocol.MaxPayloadBytes.Should().Be(1_048_576);
        Hl7ImportProtocol.MaxDiagnoses.Should().Be(100);
        Hl7ImportProtocol.FrameStart.Should().Be(0x0B);
        Hl7ImportProtocol.FrameEnd.Should().Be(0x1C);
        Hl7ImportProtocol.SegmentTerminator.Should().Be(0x0D);
    }

    [Fact]
    public void SourceManifestPinsTheOriginalXmlAndItsCompletePopulation()
    {
        var manifest = Manifest();
        Text(manifest, "contractVersion").Should().Be(Hl7ImportProtocol.ContractVersion);
        var source = manifest["source"]!;
        var sourceBytes = File.ReadAllBytes(FixturePath(Text(source, "file")));

        Convert.ToHexStringLower(SHA256.HashData(sourceBytes)).Should().Be(Text(source, "sha256"));
        Text(source, "sha256").Should().Be("4f822687e72db591d0263ad0c08449da68283327812452a5a020df530fbd0ca3");
        sourceBytes.Should().HaveCount(43_213);
        source["patientCount"]!.GetValue<int>().Should().Be(50);
        source["diagnosisCount"]!.GetValue<int>().Should().Be(100);
        source["distinctCodeCount"]!.GetValue<int>().Should().Be(25);
        source["sizeBytes"]!.GetValue<int>().Should().Be(sourceBytes.Length);
        manifest["limits"]!["maxPayloadBytes"]!.GetValue<int>().Should().Be(Hl7ImportProtocol.MaxPayloadBytes);
        manifest["limits"]!["maxDiagnosesPerPatient"]!.GetValue<int>().Should().Be(Hl7ImportProtocol.MaxDiagnoses);

        var patients = XDocument.Parse(new UTF8Encoding(false, true).GetString(sourceBytes)).Root!;
        patients.Name.Should().Be(XName.Get("Patients"));
        patients.Elements("Patient").Should().HaveCount(50);
        patients.Descendants("diagnosis").Should().HaveCount(100);
        patients.Descendants("icdCode").Select(x => x.Value).Distinct().Should().HaveCount(25);
        patients.Elements("Patient").Select(x => XmlText(x, "id")).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ManifestFilesArePortableAndMatchTheirPublishedChecksums()
    {
        var manifest = Manifest();
        Text(manifest, "sourceSystem").Should().Be(Hl7ImportProtocol.SourceSystem);
        Text(manifest, "sourceTimeZone").Should().Be(Hl7ImportProtocol.DefaultSourceTimeZone);
        manifest["defaultMllpPort"]!.GetValue<int>().Should().Be(Hl7ImportProtocol.DefaultMllpPort);
        manifest["normalizedPatients"]!["indexBase"]!.GetValue<int>().Should().Be(0);
        foreach (var file in manifest["files"]!.AsObject())
        {
            Path.IsPathRooted(file.Key).Should().BeFalse();
            file.Key.Should().NotContain("..");
            var bytes = File.ReadAllBytes(FixturePath(file.Key));
            bytes.Should().HaveCount(file.Value!["sizeBytes"]!.GetValue<int>());
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(Text(file.Value!, "sha256"));
        }

        var cases = manifest["cases"]!.AsArray();
        cases.Select(x => Text(x!, "id")).Should().OnlyHaveUniqueItems();
        cases.Select(x => Text(x!, "requestMessageControlId")).Should().OnlyHaveUniqueItems();
        cases.Select(x => Text(x!, "expectation")).Distinct().Should().BeEquivalentTo(
            new[] { "accepted", "duplicate", "validation", "auth_rejected", "protocol_rejected", "probe",
                "internal_error", "invalid_ack_correlation", "invalid_ack_result" });
    }

    [Fact]
    public void EveryNormalizedPatientAndDiagnosisPreservesTheFullXmlValues()
    {
        var manifest = Manifest();
        var sourcePatients = XDocument.Load(FixturePath(Text(manifest["source"]!, "file")))
            .Root!.Elements("Patient").ToArray();
        var normalized = ReadJson(Text(manifest["normalizedPatients"]!, "file"));
        Text(normalized, "sourceSystem").Should().Be(Hl7ImportProtocol.SourceSystem);
        Text(normalized, "sourceTimeZone").Should().Be(Hl7ImportProtocol.DefaultSourceTimeZone);
        var patients = normalized["patients"]!.Deserialize<ImportedPatientInput[]>(JsonOptions)!;

        patients.Should().HaveCount(50);
        patients.Sum(x => x.Diagnoses.Count).Should().Be(100);
        patients.SelectMany(x => x.Diagnoses).Select(x => x.Code).Distinct().Should().HaveCount(25);
        patients.Count(x => x.Sex == AdministrativeSex.Male).Should().Be(25);
        patients.Count(x => x.Sex == AdministrativeSex.Female).Should().Be(25);
        patients.GroupBy(x => x.Diagnoses.Count).ToDictionary(x => x.Key, x => x.Count())
            .Should().BeEquivalentTo(new Dictionary<int, int> { [0] = 5, [1] = 13, [2] = 14, [3] = 13, [4] = 5 });

        for (var index = 0; index < sourcePatients.Length; index++)
        {
            var xml = sourcePatients[index];
            var patient = patients[index];
            patient.SourceSystem.Should().Be(Hl7ImportProtocol.SourceSystem);
            patient.ExternalPatientId.Should().Be(XmlText(xml, "id"));
            patient.FirstName.Should().Be(XmlText(xml, "firstName"));
            patient.LastName.Should().Be(XmlText(xml, "lastName"));
            patient.MiddleName.Should().Be(XmlText(xml, "patronymicName"));
            patient.BirthDate.Should().Be(DateOnly.ParseExact(XmlText(xml, "birthDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture));
            patient.Sex.Should().Be(XmlText(xml, "sex") == "М" ? AdministrativeSex.Male : AdministrativeSex.Female);
            var sourceDiagnoses = xml.Element("diagnoses")!.Elements("diagnosis").ToArray();
            patient.Diagnoses.Should().HaveCount(sourceDiagnoses.Length);

            for (var diagnosisIndex = 0; diagnosisIndex < sourceDiagnoses.Length; diagnosisIndex++)
            {
                var diagnosis = patient.Diagnoses[diagnosisIndex];
                var sourceDiagnosis = sourceDiagnoses[diagnosisIndex];
                diagnosis.Code.Should().Be(XmlText(sourceDiagnosis, "icdCode"));
                diagnosis.Title.Should().Be(XmlText(sourceDiagnosis, "icdTitle"));
                diagnosis.CodingSystem.Should().Be(Hl7ImportProtocol.DiagnosisCodingSystem);
                diagnosis.DiagnosisType.Should().Be(Hl7ImportProtocol.UnspecifiedDiagnosisType);
                var localTime = DateTime.ParseExact(XmlText(sourceDiagnosis, "diagnosed_at"),
                    "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
                var expectedTime = new DateTimeOffset(localTime, TimeSpan.FromHours(3));
                diagnosis.DiagnosedAt.Should().Be(expectedTime);
                diagnosis.DiagnosedAt.Offset.Should().Be(TimeSpan.FromHours(3));
                diagnosis.DiagnosedAt.DateTime.Should().Be(localTime);
                diagnosis.DiagnosedAt.ToUniversalTime().Should().Be(expectedTime.ToUniversalTime());
                diagnosis.DiagnosedAt.ToUniversalTime().ToOffset(TimeSpan.FromHours(3)).DateTime.Should().Be(localTime);
                diagnosis.DiagnosedAt.Year.Should().BeInRange(2021, 2024);
            }

            var roundTrip = JsonSerializer.Deserialize<ImportedPatientInput>(JsonSerializer.Serialize(patient, JsonOptions), JsonOptions)!;
            roundTrip.Should().BeEquivalentTo(patient);
            roundTrip.Diagnoses.All(x => x.DiagnosedAt.Offset == TimeSpan.FromHours(3)).Should().BeTrue();
        }

        normalized.ToJsonString().Should().NotContain("addedAtUtc").And.NotContain("occurredAtUtc");
    }

    [Theory]
    [InlineData("sourceSystem")]
    [InlineData("externalPatientId")]
    [InlineData("firstName")]
    [InlineData("lastName")]
    [InlineData("birthDate")]
    [InlineData("sex")]
    [InlineData("diagnoses")]
    public void MissingRequiredPatientFieldsCannotBecomeDefaultClinicalValues(string property)
    {
        var patient = FirstNormalizedPatientWithDiagnoses();
        patient.Remove(property).Should().BeTrue();

        Action deserialize = () => patient.Deserialize<ImportedPatientInput>(JsonOptions);

        deserialize.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("code")]
    [InlineData("title")]
    [InlineData("codingSystem")]
    [InlineData("diagnosedAt")]
    [InlineData("diagnosisType")]
    public void MissingRequiredDiagnosisFieldsCannotBecomeDefaultClinicalValues(string property)
    {
        var diagnosis = FirstNormalizedPatientWithDiagnoses()["diagnoses"]![0]!.AsObject();
        diagnosis.Remove(property).Should().BeTrue();

        Action deserialize = () => diagnosis.Deserialize<ImportedDiagnosisInput>(JsonOptions);

        deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public void IncomingMessageSerializationAndDefaultDiagnosticsKeepTheCredentialPrivate()
    {
        const string credential = "SECRET-LOCAL-PRIVACY-TEST-CREDENTIAL";
        var message = new Hl7IncomingMessage(credential)
        {
            Header = new Hl7MessageHeader
            {
                MessageType = "NMD",
                TriggerEvent = "N02",
                MessageStructure = "NMD_N02",
                ControlId = "privacy-probe-001",
                SendingApplication = Hl7ImportProtocol.SendingApplication,
                SendingFacility = Hl7ImportProtocol.SourceSystem,
                ReceivingApplication = Hl7ImportProtocol.ReceivingApplication,
                ReceivingFacility = Hl7ImportProtocol.ReceivingFacility,
                SentAt = DateTimeOffset.Parse("2026-11-13T12:00:00Z", CultureInfo.InvariantCulture)
            }
        };

        var json = JsonSerializer.Serialize(message, JsonOptions);

        JsonNode.Parse(json)!["accessToken"].Should().BeNull();
        json.Should().NotContain(credential);
        message.ToString().Should().NotContain(credential);
        message.Patient.Should().BeNull();
        message.AccessToken.Should().Be(credential);
        JsonNode.Parse(json)!["header"]!["controlId"]!.GetValue<string>().Should().Be("privacy-probe-001");
    }

    [Theory]
    [MemberData(nameof(WireCases))]
    public void WireArtifactsUseCrTerminatedUtf8AndExplicitMllpBytes(string caseId)
    {
        var testCase = Case(caseId);
        var request = AssertPayload(Text(testCase, "request"));
        var frame = File.ReadAllBytes(FixturePath(Text(testCase, "frame")));
        var expectedFrame = new byte[] { Hl7ImportProtocol.FrameStart }.Concat(request)
            .Concat(new byte[] { Hl7ImportProtocol.FrameEnd, Hl7ImportProtocol.SegmentTerminator }).ToArray();

        if (testCase["framingError"] is not null)
        {
            Text(testCase, "framingError").Should().Be("missing-terminal-CR");
            frame.Should().Equal(expectedFrame[..^1]);
            testCase["acknowledgementExpected"]!.GetValue<bool>().Should().BeFalse();
            testCase["response"].Should().BeNull();
            Text(testCase, "expectedTransportAction").Should().Be("close-with-safe-diagnostic-without-ack");
            return;
        }

        frame.Should().Equal(expectedFrame);
        testCase["acknowledgementExpected"]!.GetValue<bool>().Should().BeTrue();
        var response = AssertPayload(Text(testCase, "response"));
        File.ReadAllBytes(FixturePath(Text(testCase, "responseFrame"))).Should().Equal(
            new byte[] { Hl7ImportProtocol.FrameStart }.Concat(response)
                .Concat(new byte[] { Hl7ImportProtocol.FrameEnd, Hl7ImportProtocol.SegmentTerminator }));
    }

    [Theory]
    [MemberData(nameof(WireCases))]
    public void RequestHeaderPositionsAndUacEdComponentsMatchTheProfile(string caseId)
    {
        var testCase = Case(caseId);
        var segments = ReadSegments(Text(testCase, "request"));
        var msh = segments[0];
        msh.Should().StartWith("MSH|^~\\&|");
        MshField(msh, 3).Should().Be(Hl7ImportProtocol.SendingApplication);
        MshField(msh, 4).Should().Be(Hl7ImportProtocol.SourceSystem);
        MshField(msh, 5).Should().Be(Hl7ImportProtocol.ReceivingApplication);
        MshField(msh, 6).Should().Be(Hl7ImportProtocol.ReceivingFacility);
        ParseWireTime(MshField(msh, 7)).Offset.Should().Be(TimeSpan.Zero);
        MshField(msh, 9).Should().Be(caseId == "probe"
            ? Hl7ImportProtocol.ProbeMessageType : Hl7ImportProtocol.PatientMessageType);
        MshField(msh, 10).Should().Be(Text(testCase, "requestMessageControlId"));
        MshField(msh, 11).Should().Be("P");
        MshField(msh, 12).Should().Be(caseId == "protocol-unsupported-version" ? "2.7" : Hl7ImportProtocol.Version);
        MshField(msh, 15).Should().BeEmpty();
        MshField(msh, 16).Should().BeEmpty();
        MshField(msh, 18).Should().Be(Hl7ImportProtocol.CharacterSet);

        segments[1].Should().StartWith("UAC|");
        Field(segments[1], 1).Should().Be(Hl7ImportProtocol.CredentialType);
        var credential = Field(segments[1], 2).Split('^');
        credential.Should().HaveCount(5);
        credential.Take(4).Should().Equal("MedSoftDesktop", "application", "jwt", "Base64");
        if (caseId == "auth-invalid-uac-base64")
        {
            Action decode = () => Convert.FromBase64String(credential[4]);
            decode.Should().Throw<FormatException>();
            return;
        }

        var fakeCredential = Manifest()["syntheticCredential"]!;
        fakeCredential["validJwt"]!.GetValue<bool>().Should().BeFalse();
        credential[4].Should().Be(Text(fakeCredential, "base64Value"));
        var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(credential[4]));
        decoded.Should().Be(Text(fakeCredential, "rawValue"));
        decoded.Should().NotStartWith("Bearer ");
        decoded.Split('.').Should().NotHaveCount(3);
    }

    [Theory]
    [MemberData(nameof(NormalizedWireCases))]
    public void AdtFixtureFieldsRoundTripEveryNormalizedPatientAndDiagnosisValue(string caseId)
    {
        var testCase = Case(caseId);
        var expectedPatient = ExpectedPatient(testCase);
        var segments = ReadSegments(Text(testCase, "request"));
        segments.Take(5).Select(x => x[..3]).Should().Equal("MSH", "UAC", "EVN", "PID", "PV1");
        segments.Skip(5).All(x => x.StartsWith("DG1|", StringComparison.Ordinal)).Should().BeTrue();
        Field(segments[2], 2).Should().Be(MshField(segments[0], 7));
        Field(segments[4], 2).Should().Be("N");
        var pid = segments[3];
        var identity = Field(pid, 3).Split('^');
        identity.Should().HaveCount(5);
        UnescapeFixtureData(identity[0]).Should().Be(expectedPatient.ExternalPatientId);
        identity[3].Should().Be(expectedPatient.SourceSystem);
        identity[4].Should().Be("PI");
        Text(testCase["normalizedPatient"]!, "externalPatientId").Should().Be(expectedPatient.ExternalPatientId);
        MshField(segments[0], 10).Should().NotBe(expectedPatient.ExternalPatientId);
        var name = Field(pid, 5).Split('^');
        name.Should().HaveCount(3);
        UnescapeFixtureData(name[0]).Should().Be(expectedPatient.LastName);
        UnescapeFixtureData(name[1]).Should().Be(expectedPatient.FirstName);
        UnescapeFixtureData(name[2]).Should().Be(expectedPatient.MiddleName ?? "");
        Field(pid, 7).Should().Be(expectedPatient.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Field(pid, 8).Should().Be(expectedPatient.Sex == AdministrativeSex.Male ? "M"
            : expectedPatient.Sex == AdministrativeSex.Female ? "F" : "U");
        var diagnoses = segments.Skip(5).ToArray();
        diagnoses.Should().HaveCount(expectedPatient.Diagnoses.Count);
        diagnoses.Length.Should().BeLessThanOrEqualTo(Hl7ImportProtocol.MaxDiagnoses);

        for (var index = 0; index < diagnoses.Length; index++)
        {
            var expected = expectedPatient.Diagnoses[index];
            var diagnosis = diagnoses[index];
            Field(diagnosis, 1).Should().Be((index + 1).ToString(CultureInfo.InvariantCulture));
            var code = Field(diagnosis, 3).Split('^');
            code.Should().HaveCount(3);
            UnescapeFixtureData(code[0]).Should().Be(expected.Code);
            UnescapeFixtureData(code[1]).Should().Be(expected.Title);
            code[2].Should().Be(expected.CodingSystem);
            var clinicalTime = ParseWireTime(Field(diagnosis, 5));
            clinicalTime.Should().Be(expected.DiagnosedAt);
            clinicalTime.Offset.Should().Be(expected.DiagnosedAt.Offset);
            clinicalTime.DateTime.Should().Be(expected.DiagnosedAt.DateTime);
            clinicalTime.Should().BeBefore(ParseWireTime(Field(segments[2], 2)));
            Field(diagnosis, 6).Split('^').Should().Equal(expected.DiagnosisType, "Unspecified", "99MEDSOFT");
        }
    }

    [Fact]
    public void SyntheticFixturePreservesLeadingZeroIdentityAllEscapesAndRepeatedClinicalCode()
    {
        var testCase = Case("accepted-escaped-synthetic");
        var expected = ExpectedPatient(testCase);
        expected.ExternalPatientId.Should().Be("000042");
        expected.LastName.Should().Contain("|^~\\&");
        expected.Diagnoses.Should().HaveCount(2);
        expected.Diagnoses.Select(x => x.Code).Distinct().Should().ContainSingle();
        expected.Diagnoses.Select(x => x.DiagnosedAt).Should().OnlyHaveUniqueItems();
        var payload = File.ReadAllText(FixturePath(Text(testCase, "request")));
        foreach (var escape in new[] { "\\F\\", "\\S\\", "\\R\\", "\\E\\", "\\T\\" })
        {
            payload.Should().Contain(escape);
        }

        var diagnoses = ReadSegments(Text(testCase, "request")).Where(x => x.StartsWith("DG1|", StringComparison.Ordinal)).ToArray();
        diagnoses.Select(x => Field(x, 3)).Distinct().Should().ContainSingle();
        diagnoses.Select(x => Field(x, 5)).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(AcknowledgedWireCases))]
    public void AckArtifactsMatchTheDeclaredFieldsAndResultSemantics(string caseId)
    {
        var testCase = Case(caseId);
        var expected = testCase["expectedAck"]!;
        var request = ReadSegments(Text(testCase, "request"));
        var response = ReadSegments(Text(testCase, "response"));
        var msh = response[0];
        var msa = response[1];
        MshField(msh, 3).Should().Be(MshField(request[0], 5));
        MshField(msh, 4).Should().Be(MshField(request[0], 6));
        MshField(msh, 5).Should().Be(MshField(request[0], 3));
        MshField(msh, 6).Should().Be(MshField(request[0], 4));
        MshField(msh, 9).Should().Be(Text(expected, "messageType"));
        MshField(msh, 9).Split('^').Should().Equal("ACK", MshField(request[0], 9).Split('^')[1], "ACK");
        MshField(msh, 10).Should().Be(Text(expected, "messageControlId")).And.NotBe(MshField(request[0], 10));
        MshField(msh, 12).Should().Be(Hl7ImportProtocol.Version);
        MshField(msh, 18).Should().Be(Hl7ImportProtocol.CharacterSet);
        msa.Should().StartWith("MSA|");
        Field(msa, 1).Should().Be(Text(expected, "code"));
        Field(msa, 2).Should().Be(Text(expected, "correlationId"));
        UnescapeFixtureData(Field(msa, 3)).Should().Be(Text(expected, "text"));
        if (caseId == "invalid-ack-correlation")
        {
            Field(msa, 2).Should().NotBe(MshField(request[0], 10));
            Text(testCase, "expectedCorrelationId").Should().Be(MshField(request[0], 10));
            testCase["clientSuccessExpected"]!.GetValue<bool>().Should().BeFalse();
            Text(testCase, "clientOutcome").Should().Be("unknown");
            return;
        }

        Field(msa, 2).Should().Be(MshField(request[0], 10));
        var expectation = Text(testCase, "expectation");
        switch (expectation)
        {
            case "accepted":
                Field(msa, 1).Should().Be("AA");
                Guid.TryParseExact(Field(msa, 3), "D", out var patientId).Should().BeTrue();
                patientId.Should().NotBe(Guid.Empty);
                Field(msa, 3).Should().Be(Text(testCase, "expectedPatientUuid"));
                break;
            case "probe":
                Field(msa, 1).Should().Be("AA");
                Field(msa, 3).Should().Be(Hl7ImportProtocol.ProbeSuccess);
                request.Select(x => x[..3]).Should().Equal("MSH", "UAC", "NCK");
                Field(request[2], 1).Should().Be(MshField(request[0], 7));
                testCase["patientMutationExpected"]!.GetValue<bool>().Should().BeFalse();
                break;
            case "invalid_ack_result":
                Field(msa, 1).Should().Be("AA");
                Guid.TryParseExact(Field(msa, 3), "D", out _).Should().BeFalse();
                testCase["clientSuccessExpected"]!.GetValue<bool>().Should().BeFalse();
                Text(testCase, "clientOutcome").Should().Be("unknown");
                break;
            case "duplicate":
            case "validation":
            case "internal_error":
                Field(msa, 1).Should().Be("AE");
                Text(expected, "text").Should().StartWith(expectation == "internal_error" ? "INTERNAL:"
                    : expectation.ToUpperInvariant() + ":");
                break;
            case "auth_rejected":
            case "protocol_rejected":
                Field(msa, 1).Should().Be("AR");
                Text(expected, "text").Should().StartWith(expectation == "auth_rejected" ? "AUTH:" : "PROTOCOL:");
                break;
        }

        if (expectation == "auth_rejected")
        {
            var error = response.Single(x => x.StartsWith("ERR|", StringComparison.Ordinal));
            Field(error, 3).Split('^')[0].Should().Be(Text(expected, "errorCode")).And.Be("207");
            Field(error, 4).Should().Be(Text(expected, "errorSeverity")).And.Be("E");
            UnescapeFixtureData(Field(error, 7)).Should().Be(Text(expected, "errorDiagnostic"));
        }

        var payload = string.Join('\r', response);
        payload.Should().NotContain(Text(Manifest()["syntheticCredential"]!, "rawValue"))
            .And.NotContain(Text(Manifest()["syntheticCredential"]!, "base64Value"))
            .And.NotContain("Bearer ").And.NotContain("System.").And.NotContain("SELECT ").And.NotContain("INSERT ");
    }

    [Fact]
    public void NegativeFixturesContainTheirDeclaredDefects()
    {
        var birth = ReadSegments(Text(Case("validation-invalid-birth-date"), "request"));
        var birthValue = Field(birth.Single(x => x.StartsWith("PID|", StringComparison.Ordinal)), 7);
        DateOnly.TryParseExact(birthValue, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _).Should().BeFalse();
        var diagnoses = ReadSegments(Text(Case("validation-invalid-second-diagnosis"), "request"))
            .Where(x => x.StartsWith("DG1|", StringComparison.Ordinal)).ToArray();
        diagnoses.Should().HaveCount(2);
        ParseWireTime(Field(diagnoses[0], 5)).Year.Should().Be(2023);
        Action parseSecond = () => ParseWireTime(Field(diagnoses[1], 5));
        parseSecond.Should().Throw<FormatException>();
        ReadSegments(Text(Case("protocol-missing-pid"), "request"))
            .Should().NotContain(x => x.StartsWith("PID|", StringComparison.Ordinal));
        MshField(ReadSegments(Text(Case("protocol-unsupported-version"), "request"))[0], 12)
            .Should().NotBe(Hl7ImportProtocol.Version);
    }

    public static IEnumerable<object[]> WireCases() => Manifest()["cases"]!.AsArray()
        .Select(x => new object[] { Text(x!, "id") });

    public static IEnumerable<object[]> AcknowledgedWireCases() => Manifest()["cases"]!.AsArray()
        .Where(x => x!["acknowledgementExpected"]!.GetValue<bool>())
        .Select(x => new object[] { Text(x!, "id") });

    public static IEnumerable<object[]> NormalizedWireCases() => Manifest()["cases"]!.AsArray()
        .Where(x => x!["normalizedPatient"] is not null)
        .Select(x => new object[] { Text(x!, "id") });

    private static JsonNode Case(string caseId) => Manifest()["cases"]!.AsArray().Single(x => Text(x!, "id") == caseId)!;

    private static ImportedPatientInput ExpectedPatient(JsonNode testCase)
    {
        var reference = testCase["normalizedPatient"]!;
        return ReadJson(Text(reference, "file"))["patients"]![reference["index"]!.GetValue<int>()]!
            .Deserialize<ImportedPatientInput>(JsonOptions)!;
    }

    private static byte[] AssertPayload(string file)
    {
        var bytes = File.ReadAllBytes(FixturePath(file));
        bytes.Should().NotBeEmpty();
        bytes.Length.Should().BeLessThanOrEqualTo(Hl7ImportProtocol.MaxPayloadBytes);
        bytes.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());
        bytes.Should().NotContain((byte)0x0A).And.NotContain((byte)0x00);
        bytes[^1].Should().Be(Hl7ImportProtocol.SegmentTerminator);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        text.Should().StartWith("MSH|");
        text.Split('\r')[..^1].Should().OnlyContain(x => x.Length >= 4 && x[3] == '|');
        return bytes;
    }

    // These helpers inspect the fixed fixture grammar. L4 supplies the production decoder.
    private static string[] ReadSegments(string file) => new UTF8Encoding(false, true)
        .GetString(File.ReadAllBytes(FixturePath(file))).Split('\r', StringSplitOptions.RemoveEmptyEntries);

    private static string Field(string segment, int number) => segment.Split('|')[number];

    private static string MshField(string segment, int number) => Field(segment, number - 1);

    private static string UnescapeFixtureData(string value) => Regex.Replace(value, @"\\([FSTRE])\\", match => match.Groups[1].Value switch
    {
        "F" => "|",
        "S" => "^",
        "T" => "&",
        "R" => "~",
        "E" => "\\",
        _ => throw new InvalidOperationException("Unexpected fixture escape")
    });

    private static DateTimeOffset ParseWireTime(string value) => DateTimeOffset.ParseExact(
        value.Insert(value.Length - 2, ":"), "yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static JsonObject FirstNormalizedPatientWithDiagnoses()
    {
        var normalized = ReadJson(Text(Manifest()["normalizedPatients"]!, "file"));
        return normalized["patients"]!.AsArray().Select(x => x!.AsObject())
            .First(x => x["diagnoses"]!.AsArray().Count > 0);
    }

    private static JsonObject Manifest() => ReadJson("manifest.json");

    private static JsonObject ReadJson(string file) => JsonNode.Parse(File.ReadAllText(FixturePath(file)))!.AsObject();

    private static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", file);

    private static string Text(JsonNode node, string property) => node[property]!.GetValue<string>();

    private static string XmlText(XElement element, string name) => element.Element(name)!.Value;
}
