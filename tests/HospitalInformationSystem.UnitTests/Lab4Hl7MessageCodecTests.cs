using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Contracts.Hl7Import;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7MessageCodecTests
{
    private const string FixtureCredential = "lab4-fixture-access-token-not-valid";
    private const string WireTime = "20261113120000+0000";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Hl7MessageCodec _codec = new(new FixedClock());

    [Theory]
    [InlineData("adt-no-diagnoses.hl7", "normalized-patients.json", 0)]
    [InlineData("adt-multiple-diagnoses.hl7", "normalized-patients.json", 2)]
    [InlineData("adt-escaped-synthetic.hl7", "normalized-synthetic-patients.json", 0)]
    public void Decode_AcceptedFixture_ShouldPreserveHeaderCredentialAndEveryPatientField(
        string payloadFile, string patientFile, int patientIndex)
    {
        var expected = Patients(patientFile)[patientIndex];

        var actual = _codec.Decode(Fixture(payloadFile));

        actual.Header.MessageType.Should().Be("ADT");
        actual.Header.TriggerEvent.Should().Be("A28");
        actual.Header.MessageStructure.Should().Be("ADT_A05");
        actual.Header.SendingApplication.Should().Be("MEDSOFT_IMPORT");
        actual.Header.SendingFacility.Should().Be("LAB4_XML");
        actual.Header.ReceivingApplication.Should().Be("HIS");
        actual.Header.ReceivingFacility.Should().Be("MEDSOFT");
        actual.Header.SentAt.Should().Be(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        actual.AccessToken.Should().Be(FixtureCredential);
        AssertPatient(actual.Patient, expected);
    }

    [Fact]
    public void Decode_AllSourcePatients_ShouldPreserveAllOneHundredClinicalDatesAndOffsets()
    {
        var patients = Patients("normalized-patients.json");
        patients.Should().HaveCount(50);
        patients.Sum(patient => patient.Diagnoses.Count).Should().Be(100);

        foreach (var patient in patients)
        {
            var actual = _codec.Decode(Utf8.GetBytes(Message(patient)));

            AssertPatient(actual.Patient, patient);
            actual.Header.ControlId.Should().Be("codec-" + patient.ExternalPatientId);
        }
    }

    [Fact]
    public void Decode_EscapedSyntheticFixture_ShouldKeepLeadingZeroesAndDecodeEachDelimiterOnce()
    {
        var patient = _codec.Decode(Fixture("adt-escaped-synthetic.hl7")).Patient!;

        patient.ExternalPatientId.Should().Be("000042");
        patient.LastName.Should().Be("Разделители |^~\\&");
        patient.Diagnoses.Should().HaveCount(2);
        patient.Diagnoses[0].Title.Should().Be("Тест | поле ^ компонент ~ повтор \\ escape & часть");
        patient.Diagnoses[0].Code.Should().Be(patient.Diagnoses[1].Code);
        patient.Diagnoses[0].DiagnosedAt.Should().NotBe(patient.Diagnoses[1].DiagnosedAt);
    }

    [Theory]
    [InlineData(0, 24, true)]
    [InlineData(0, 25, true)]
    [InlineData(0, 64, true)]
    [InlineData(0, 65, false)]
    [InlineData(1, 24, true)]
    [InlineData(1, 25, true)]
    [InlineData(1, 64, true)]
    [InlineData(1, 65, false)]
    [InlineData(2, 24, true)]
    [InlineData(2, 25, true)]
    [InlineData(2, 64, true)]
    [InlineData(2, 65, false)]
    public void Decode_PatientNameLengthBoundary_ShouldPreserveValidValuesAndRejectOverflow(
        int component, int length, bool accepted)
    {
        var names = new[] { "Иванов", "Алексей", "Петрович" };
        names[component] = new string('Я', length);
        var payload = Utf8.GetBytes(Field(Text("adt-no-diagnoses.hl7"), "PID", 5, string.Join('^', names)));

        if (!accepted)
        {
            var exception = Failure(payload);
            exception.Kind.Should().Be(Hl7FailureKind.Validation);
            exception.Header.Should().NotBeNull();
            exception.Header!.ControlId.Should().Be("lab4-accepted-no-diagnoses-001");
            exception.ExternalPatientId.Should().Be("347821");
            return;
        }

        var patient = _codec.Decode(payload).Patient!;

        new[] { patient.LastName, patient.FirstName, patient.MiddleName }.Should().Equal(names);
    }

    [Fact]
    public void Decode_EscapedPatientNamesAtMaximumLength_ShouldLimitDecodedTextAndPreserveEveryCharacter()
    {
        var expected = new string('Ж', 59) + "|^~\\&";
        var name = Escape(expected);
        var payload = Utf8.GetBytes(Field(Text("adt-no-diagnoses.hl7"), "PID", 5,
            string.Join('^', name, name, name)));

        var patient = _codec.Decode(payload).Patient!;

        expected.Should().HaveLength(64);
        patient.LastName.Should().Be(expected);
        patient.FirstName.Should().Be(expected);
        patient.MiddleName.Should().Be(expected);
    }

    [Fact]
    public void Decode_Probe_ShouldProduceAnAuthenticatedEnvelopeWithoutPatientData()
    {
        var payload = Fixture("probe.hl7");

        var envelope = _codec.DecodeEnvelope(payload);
        var message = _codec.Decode(payload);

        envelope.Header.Should().Be(message.Header);
        envelope.AccessToken.Should().Be(FixtureCredential);
        message.Header.MessageType.Should().Be("NMD");
        message.Header.TriggerEvent.Should().Be("N02");
        message.Header.MessageStructure.Should().Be("NMD_N02");
        message.Header.ControlId.Should().Be("lab4-probe-001");
        message.Patient.Should().BeNull();
    }

    [Fact]
    public void DecodeEnvelope_ClinicallyInvalidPatient_ShouldKeepAuthenticationAvailableBeforePatientMapping()
    {
        var payload = Fixture("adt-invalid-birth-date.hl7");

        var envelope = _codec.DecodeEnvelope(payload);

        envelope.AccessToken.Should().Be(FixtureCredential);
        envelope.Header.ControlId.Should().Be("lab4-invalid-birth-date-001");
        Failure(payload).Kind.Should().Be(Hl7FailureKind.Validation);
    }

    [Fact]
    public void DecodedObjects_ShouldOmitCredentialFromJsonAndDefaultDiagnosticText()
    {
        var payload = Fixture("adt-no-diagnoses.hl7");
        var envelope = _codec.DecodeEnvelope(payload);
        var message = _codec.Decode(payload);

        JsonSerializer.Serialize(envelope, JsonOptions).Should().NotContain(FixtureCredential);
        JsonSerializer.Serialize(message, JsonOptions).Should().NotContain(FixtureCredential);
        envelope.ToString().Should().NotContain(FixtureCredential);
        message.ToString().Should().NotContain(FixtureCredential);
    }

    [Theory]
    [InlineData("adt-unsupported-version.hl7", Hl7FailureKind.Protocol, "lab4-unsupported-version-001")]
    [InlineData("adt-missing-pid.hl7", Hl7FailureKind.Protocol, "lab4-missing-pid-001")]
    [InlineData("adt-invalid-uac-base64.hl7", Hl7FailureKind.Authentication, "lab4-invalid-uac-base64-001")]
    [InlineData("adt-invalid-birth-date.hl7", Hl7FailureKind.Validation, "lab4-invalid-birth-date-001")]
    [InlineData("adt-invalid-second-diagnosis.hl7", Hl7FailureKind.Validation, "lab4-invalid-second-diagnosis-001")]
    public void Decode_NegativeFixture_ShouldClassifyFailureAndRetainCorrelation(
        string file, Hl7FailureKind kind, string controlId)
    {
        var exception = Failure(Fixture(file));

        exception.Kind.Should().Be(kind);
        exception.Header.Should().NotBeNull();
        exception.Header!.ControlId.Should().Be(controlId);
        exception.ToString().Should().NotContain(FixtureCredential).And.NotContain("%%%NOT-BASE64%%%");
        exception.ToString().Should().NotContain("UAC|");
    }

    [Theory]
    [InlineData("unsupported-message")]
    [InlineData("unsupported-charset")]
    [InlineData("unsupported-processing-id")]
    [InlineData("enhanced-ack-mode")]
    [InlineData("wrong-source")]
    [InlineData("wrong-receiver")]
    [InlineData("missing-control-id")]
    [InlineData("invalid-message-time")]
    [InlineData("repeated-identifier")]
    [InlineData("wrong-identifier-authority")]
    [InlineData("repeated-name")]
    [InlineData("name-subcomponent")]
    [InlineData("unknown-escape")]
    [InlineData("unterminated-escape")]
    [InlineData("duplicate-pid")]
    [InlineData("unknown-segment")]
    [InlineData("out-of-order-segments")]
    [InlineData("unexpected-field")]
    public void Decode_UnsupportedProfileStructure_ShouldRejectInsteadOfSilentlyDroppingData(string problem)
    {
        var original = Text("adt-no-diagnoses.hl7");
        var pid = original.Split('\r').Single(segment => segment.StartsWith("PID|", StringComparison.Ordinal));
        var input = problem switch
        {
            "unsupported-message" => Field(original, "MSH", 9, "ORM^O01^ORM_O01"),
            "unsupported-charset" => Field(original, "MSH", 18, "ASCII"),
            "unsupported-processing-id" => Field(original, "MSH", 11, "T"),
            "enhanced-ack-mode" => Field(original, "MSH", 15, "AL"),
            "wrong-source" => Field(original, "MSH", 4, "OTHER"),
            "wrong-receiver" => Field(original, "MSH", 5, "OTHER"),
            "missing-control-id" => Field(original, "MSH", 10, ""),
            "invalid-message-time" => Field(original, "MSH", 7, "20260230120000+0000"),
            "repeated-identifier" => Field(original, "PID", 3, "347821^^^LAB4_XML^PI~999999^^^LAB4_XML^PI"),
            "wrong-identifier-authority" => Field(original, "PID", 3, "347821^^^OTHER^PI"),
            "repeated-name" => Field(original, "PID", 5, "Иванов^Алексей^Петрович~Петров^Другой"),
            "name-subcomponent" => Field(original, "PID", 5, "Иванов&Петров^Алексей^Петрович"),
            "unknown-escape" => Field(original, "PID", 5, "Иванов\\X41\\^Алексей^Петрович"),
            "unterminated-escape" => Field(original, "PID", 5, "Иванов\\F^Алексей^Петрович"),
            "duplicate-pid" => original.Replace(pid + "\r", pid + "\r" + pid + "\r", StringComparison.Ordinal),
            "unknown-segment" => original + "ZXX|UNSUPPORTED\r",
            "out-of-order-segments" => original.Replace(pid + "\rPV1||N\r", "PV1||N\r" + pid + "\r", StringComparison.Ordinal),
            "unexpected-field" => Field(original, "PID", 11, "UNSUPPORTED ADDRESS"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        Failure(Utf8.GetBytes(input)).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    [Theory]
    [InlineData("missing-uac")]
    [InlineData("duplicate-uac")]
    [InlineData("wrong-type")]
    [InlineData("wrong-media")]
    [InlineData("wrong-subtype")]
    [InlineData("wrong-encoding")]
    [InlineData("missing-data")]
    [InlineData("invalid-utf8-token")]
    [InlineData("bearer-prefix")]
    [InlineData("control-in-token")]
    public void Decode_InvalidCredentialEnvelope_ShouldRejectWithoutExposingCredential(string problem)
    {
        var original = Text("adt-no-diagnoses.hl7");
        var uac = original.Split('\r').Single(segment => segment.StartsWith("UAC|", StringComparison.Ordinal));
        var credential = Convert.ToBase64String(Utf8.GetBytes(FixtureCredential));
        var input = problem switch
        {
            "missing-uac" => original.Replace(uac + "\r", "", StringComparison.Ordinal),
            "duplicate-uac" => original.Replace(uac + "\r", uac + "\r" + uac + "\r", StringComparison.Ordinal),
            "wrong-type" => Field(original, "UAC", 1, "APIKEY^API key^99MEDSOFT"),
            "wrong-media" => Field(original, "UAC", 2, "MedSoftDesktop^text^jwt^Base64^" + credential),
            "wrong-subtype" => Field(original, "UAC", 2, "MedSoftDesktop^application^json^Base64^" + credential),
            "wrong-encoding" => Field(original, "UAC", 2, "MedSoftDesktop^application^jwt^A^" + credential),
            "missing-data" => Field(original, "UAC", 2, "MedSoftDesktop^application^jwt^Base64^"),
            "invalid-utf8-token" => Field(original, "UAC", 2, "MedSoftDesktop^application^jwt^Base64^/w=="),
            "bearer-prefix" => Field(original, "UAC", 2, "MedSoftDesktop^application^jwt^Base64^" +
                Convert.ToBase64String(Utf8.GetBytes("Bearer " + FixtureCredential))),
            "control-in-token" => Field(original, "UAC", 2, "MedSoftDesktop^application^jwt^Base64^" +
                Convert.ToBase64String(Utf8.GetBytes(FixtureCredential + "\rsecret"))),
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        var exception = Failure(Utf8.GetBytes(input));

        exception.Kind.Should().Be(Hl7FailureKind.Authentication);
        exception.ToString().Should().NotContain(FixtureCredential).And.NotContain(credential);
        exception.ToString().Should().NotContain("secret").And.NotContain("UAC|");
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("wrong-coding-system")]
    [InlineData("wrong-diagnosis-type")]
    [InlineData("repeated-diagnosis-value")]
    public void Decode_UnsupportedDiagnosisStructure_ShouldReject(string problem)
    {
        var original = Text("adt-multiple-diagnoses.hl7");
        var input = problem switch
        {
            "ordinal" => Field(original, "DG1", 1, "2"),
            "wrong-coding-system" => Field(original, "DG1", 3, "I10^Диагноз^ICD9"),
            "wrong-diagnosis-type" => Field(original, "DG1", 6, "F^Final^99MEDSOFT"),
            "repeated-diagnosis-value" => Field(original, "DG1", 3, "I10^Диагноз^I10~E11^Второй^I10"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        Failure(Utf8.GetBytes(input)).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    [Theory]
    [InlineData("20230514102000")]
    [InlineData("20230514102000+1500")]
    [InlineData("20230229102000+0300")]
    [InlineData("20230514102000.123+0300")]
    public void Decode_InvalidClinicalTimestamp_ShouldRejectWithoutFallingBackToImportTime(string timestamp)
    {
        var payload = Field(Text("adt-multiple-diagnoses.hl7"), "DG1", 5, timestamp);

        Failure(Utf8.GetBytes(payload)).Kind.Should().Be(Hl7FailureKind.Validation);
    }

    [Theory]
    [InlineData("AB", "Диагноз")]
    [InlineData("I10", "Да")]
    public void Decode_DiagnosisBelowMinimumTextLength_ShouldRejectClinicalData(string code, string title)
    {
        var payload = Field(Text("adt-multiple-diagnoses.hl7"), "DG1", 3, code + "^" + title + "^I10");

        var exception = Failure(Utf8.GetBytes(payload));

        exception.Kind.Should().Be(Hl7FailureKind.Validation);
        exception.Header.Should().NotBeNull();
        exception.Header!.ControlId.Should().Be("lab4-accepted-multiple-diagnoses-001");
    }

    [Fact]
    public void Decode_DiagnosisAtMaximumTextLengths_ShouldPreserveCompleteValues()
    {
        var patient = Patients("normalized-patients.json")[2];
        var diagnosis = patient.Diagnoses[0] with { Code = new string('A', 32), Title = new string('Н', 256) };
        patient = patient with { Diagnoses = [diagnosis] };

        var actual = _codec.Decode(Utf8.GetBytes(Message(patient)));

        AssertPatient(actual.Patient, patient);
        actual.Patient!.Diagnoses.Single().Code.Should().HaveLength(32);
        actual.Patient.Diagnoses.Single().Title.Should().HaveLength(256);
    }

    [Fact]
    public void Decode_MalformedUacEscape_ShouldRetainCorrelationAndRejectAuthentication()
    {
        var payload = Field(Text("adt-no-diagnoses.hl7"), "UAC", 2, "\\Q\\");

        var exception = Failure(Utf8.GetBytes(payload));

        exception.Kind.Should().Be(Hl7FailureKind.Authentication);
        exception.Header.Should().NotBeNull();
        exception.Header!.ControlId.Should().Be("lab4-accepted-no-diagnoses-001");
        exception.ToString().Should().NotContain("\\Q\\").And.NotContain("UAC|");
    }

    [Fact]
    public void Decode_UnsupportedMessageWithReadableTriggerAndEmptyStructure_ShouldRetainAckCorrelation()
    {
        var payload = Field(Text("adt-no-diagnoses.hl7"), "MSH", 9, "ORM^O01^");

        var exception = Failure(Utf8.GetBytes(payload));

        exception.Kind.Should().Be(Hl7FailureKind.Protocol);
        exception.Header.Should().NotBeNull();
        exception.Header!.ControlId.Should().Be("lab4-accepted-no-diagnoses-001");
        exception.Header.MessageType.Should().Be("ORM");
        exception.Header.TriggerEvent.Should().Be("O01");
        exception.Header.MessageStructure.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ADT^A28^ADT_A05^UNSUPPORTED")]
    [InlineData("ADT^A28^ADT_A05&UNSUPPORTED")]
    public void Decode_UnsupportedMessageStructure_ShouldRetainReadableCorrelation(string messageType)
    {
        var payload = Field(Text("adt-no-diagnoses.hl7"), "MSH", 9, messageType);

        var exception = Failure(Utf8.GetBytes(payload));

        exception.Kind.Should().Be(Hl7FailureKind.Protocol);
        exception.Header.Should().NotBeNull();
        exception.Header!.ControlId.Should().Be("lab4-accepted-no-diagnoses-001");
        exception.Header.MessageType.Should().Be("ADT");
        exception.Header.TriggerEvent.Should().Be("A28");
        var readEnvelope = () => _codec.DecodeEnvelope(Utf8.GetBytes(payload));
        readEnvelope.Should().Throw<Hl7DecodeException>().Which.Header.Should().NotBeNull();
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("line-feed")]
    [InlineData("missing-final-cr")]
    [InlineData("empty-segment")]
    [InlineData("nul")]
    [InlineData("mllp-markers")]
    public void Decode_InvalidWireEncoding_ShouldReject(string problem)
    {
        var original = Text("adt-no-diagnoses.hl7");
        var input = problem switch
        {
            "bom" => "\uFEFF" + original,
            "line-feed" => original.Replace("\r", "\n", StringComparison.Ordinal),
            "missing-final-cr" => original.TrimEnd('\r'),
            "empty-segment" => original + "\r",
            "nul" => original.Replace("Иванов", "Иван\0ов", StringComparison.Ordinal),
            "mllp-markers" => "\u000B" + original + "\u001C\r",
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        Failure(Utf8.GetBytes(input)).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    [Fact]
    public void Decode_InvalidUtf8Bytes_ShouldRejectInsteadOfUsingReplacementCharacters()
    {
        var prefix = Utf8.GetBytes(Text("adt-no-diagnoses.hl7"));
        var payload = prefix.Concat(new byte[] { 0xC3, 0x28 }).ToArray();

        Failure(payload).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    [Fact]
    public void Decode_OversizedPayload_ShouldReject()
    {
        var payload = new byte[Hl7ImportProtocol.MaxPayloadBytes + 1];
        Array.Fill(payload, (byte)'A');

        Failure(payload).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Decode_DiagnosisCountLimit_ShouldAcceptBoundaryAndRejectOverflow(int count, bool accepted)
    {
        var patient = Patients("normalized-patients.json")[2];
        patient = patient with { Diagnoses = Enumerable.Repeat(patient.Diagnoses[0], count).ToArray() };
        var payload = Utf8.GetBytes(Message(patient));

        if (accepted)
            _codec.Decode(payload).Patient!.Diagnoses.Should().HaveCount(count);
        else
            Failure(payload).Kind.Should().Be(Hl7FailureKind.Protocol);
    }

    private Hl7DecodeException Failure(byte[] payload)
    {
        Action action = () => _codec.Decode(payload);
        return action.Should().Throw<Hl7DecodeException>().Which;
    }

    private static void AssertPatient(ImportedPatientInput? actual, ImportedPatientInput expected)
    {
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        for (var index = 0; index < expected.Diagnoses.Count; index++)
        {
            actual!.Diagnoses[index].DiagnosedAt.Offset.Should().Be(expected.Diagnoses[index].DiagnosedAt.Offset);
            actual.Diagnoses[index].DiagnosedAt.DateTime.Should().Be(expected.Diagnoses[index].DiagnosedAt.DateTime);
        }
    }

    private static ImportedPatientInput[] Patients(string file)
    {
        using var document = JsonDocument.Parse(Fixture(file));
        return document.RootElement.GetProperty("patients").Deserialize<ImportedPatientInput[]>(JsonOptions)!;
    }

    private static string Message(ImportedPatientInput patient)
    {
        var builder = new StringBuilder();
        builder.Append("MSH|^~\\&|MEDSOFT_IMPORT|LAB4_XML|HIS|MEDSOFT|").Append(WireTime)
            .Append("||ADT^A28^ADT_A05|codec-").Append(Escape(patient.ExternalPatientId))
            .Append("|P|2.8||||||UNICODE UTF-8\r");
        builder.Append("UAC|JWT^JSON Web Token^99MEDSOFT|MedSoftDesktop^application^jwt^Base64^")
            .Append(Convert.ToBase64String(Utf8.GetBytes(FixtureCredential))).Append('\r');
        builder.Append("EVN||").Append(WireTime).Append('\r');
        builder.Append("PID|||").Append(Escape(patient.ExternalPatientId)).Append("^^^LAB4_XML^PI||")
            .Append(Escape(patient.LastName)).Append('^').Append(Escape(patient.FirstName)).Append('^')
            .Append(Escape(patient.MiddleName ?? "")).Append("||")
            .Append(patient.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append('|')
            .Append(patient.Sex switch
            {
                Domain.Patients.AdministrativeSex.Male => 'M',
                Domain.Patients.AdministrativeSex.Female => 'F',
                _ => 'U'
            }).Append("\rPV1||N\r");
        for (var index = 0; index < patient.Diagnoses.Count; index++)
        {
            var diagnosis = patient.Diagnoses[index];
            builder.Append("DG1|").Append(index + 1).Append("||").Append(Escape(diagnosis.Code)).Append('^')
                .Append(Escape(diagnosis.Title)).Append("^I10||")
                .Append(diagnosis.DiagnosedAt.ToString("yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture)
                    .Replace(":", "", StringComparison.Ordinal))
                .Append("|U^Unspecified^99MEDSOFT\r");
        }
        return builder.ToString();
    }

    private static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var value in text)
            result.Append(value switch
            {
                '|' => "\\F\\",
                '^' => "\\S\\",
                '~' => "\\R\\",
                '\\' => "\\E\\",
                '&' => "\\T\\",
                _ => value.ToString()
            });
        return result.ToString();
    }

    private static string Field(string message, string segmentName, int field, string value)
    {
        var segments = message.Split('\r');
        var segmentIndex = Array.FindIndex(segments, segment => segment.StartsWith(segmentName + "|", StringComparison.Ordinal));
        var fields = segments[segmentIndex].Split('|');
        var index = segmentName == "MSH" ? field - 1 : field;
        if (fields.Length <= index) Array.Resize(ref fields, index + 1);
        fields[index] = value;
        segments[segmentIndex] = string.Join('|', fields);
        return string.Join('\r', segments);
    }

    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", file));
    private static string Text(string file) => Utf8.GetString(Fixture(file));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    }
}
