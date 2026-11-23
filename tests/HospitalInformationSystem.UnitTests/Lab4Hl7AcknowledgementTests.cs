using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Models.Hl7Import;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7AcknowledgementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 13, 14, 15, 678, TimeSpan.FromHours(3));
    private static readonly Guid PatientId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private readonly Hl7AcknowledgementEncoder _encoder = new(new FrozenTimeProvider());

    [Fact]
    public void Encode_ImportedPatient_ShouldWriteCorrelatedUtf8PayloadWithIndependentId()
    {
        var request = Header();
        var bytes = _encoder.Encode(request, Hl7AcknowledgementFactory.Imported(new PatientImported(PatientId, 3)));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var segments = text.Split('\r');
        var msh = segments[0].Split('|');
        var msa = segments[1].Split('|');

        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        text.Should().NotContain("\n").And.NotContain("\0");
        text.Should().EndWith("\r");
        segments.Should().HaveCount(3);
        segments[2].Should().BeEmpty();
        msh.Should().HaveCount(18);
        msh[0].Should().Be("MSH");
        msh[1].Should().Be("^~\\&");
        msh[2..6].Should().Equal("HIS", "MEDSOFT", "MEDSOFT_IMPORT", "LAB4_XML");
        msh[6].Should().Be("20261117101415+0000");
        msh[8].Should().Be("ACK^A28^ACK");
        Guid.TryParseExact(msh[9], "N", out var ackId).Should().BeTrue();
        ackId.Should().NotBeEmpty();
        msh[9].Should().NotBe(request.ControlId);
        msh[10].Should().Be("P");
        msh[11].Should().Be("2.8");
        msh[12..17].Should().OnlyContain(value => value == "");
        msh[17].Should().Be("UNICODE UTF-8");
        msa.Should().Equal("MSA", "AA", request.ControlId, PatientId.ToString("D"));
    }

    [Fact]
    public void Encode_EachAcknowledgement_ShouldReceiveNewControlId()
    {
        var request = Header();
        var acknowledgement = Hl7AcknowledgementFactory.ProtocolFailure();

        var first = Text(_encoder.Encode(request, acknowledgement)).Split('\r')[0].Split('|')[9];
        var second = Text(_encoder.Encode(request, acknowledgement)).Split('\r')[0].Split('|')[9];

        first.Should().NotBe(second).And.NotBe(request.ControlId);
        second.Should().NotBe(request.ControlId);
    }

    [Fact]
    public void Encode_AuthenticationFailure_ShouldMatchC4AuthMsaAndErr()
    {
        var request = Header() with { ControlId = "lab4-auth-rejected-001" };
        var actual = Text(_encoder.Encode(request, Hl7AcknowledgementFactory.AuthenticationFailure()));
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", "ack-auth-rejected.hl7"));
        var expectedBody = fixture[(fixture.IndexOf('\r') + 1)..];

        actual[(actual.IndexOf('\r') + 1)..].Should().Be(expectedBody);
        actual.Should().Contain("ERR|||207^Application internal error^HL70357|E|||Доступ запрещён\r");
    }

    [Fact]
    public void Encode_ProbeSuccess_ShouldUseN02AndConnectedWithoutPatientId()
    {
        var request = Header() with
        {
            MessageType = "NMD", TriggerEvent = "N02", MessageStructure = "NMD_N02", ControlId = "probe-001"
        };

        var actual = Text(_encoder.Encode(request, Hl7AcknowledgementFactory.ProbeAccepted()));

        actual.Should().Contain("|ACK^N02^ACK|");
        actual.Should().EndWith("MSA|AA|probe-001|CONNECTED\r");
        actual.Should().NotContain("ERR|");
    }

    [Fact]
    public void Encode_ReservedHeaderCharacters_ShouldEscapeAtomsAndKeepStructuralDelimiters()
    {
        var request = Header() with
        {
            ControlId = "id|^~\\&", SendingApplication = "desktop|client", SendingFacility = "source^branch",
            ReceivingApplication = "his&server", ReceivingFacility = "facility~room", TriggerEvent = "A|8"
        };

        var actual = Text(_encoder.Encode(request, Hl7AcknowledgementFactory.ProtocolFailure()));

        actual.Should().StartWith("MSH|^~\\&|his\\T\\server|facility\\R\\room|desktop\\F\\client|source\\S\\branch|");
        actual.Should().Contain("|ACK^A\\F\\8^ACK|");
        actual.Should().Contain("MSA|AR|id\\F\\\\S\\\\R\\\\E\\\\T\\|PROTOCOL:");
        actual.Split('\r')[0].Split('|').Should().HaveCount(18);
        actual.Split('\r')[1].Split('|').Should().HaveCount(4);
    }

    [Theory]
    [InlineData("A08", "ADT_A01")]
    [InlineData("A08", "")]
    public void Encode_ProtocolFailureForUnsupportedStructure_ShouldKeepReadableTrigger(string trigger, string structure)
    {
        var request = Header() with { TriggerEvent = trigger, MessageStructure = structure };

        var actual = Text(_encoder.Encode(request, Hl7AcknowledgementFactory.ProtocolFailure()));

        actual.Should().Contain("|ACK^A08^ACK|");
        actual.Should().Contain("MSA|AR|request-001|PROTOCOL:");
    }

    [Theory]
    [InlineData(PatientImportRejection.Duplicate, "DUPLICATE: пациент уже импортирован")]
    [InlineData(PatientImportRejection.Validation, "VALIDATION: проверьте данные пациента и диагнозов")]
    public void Rejected_ShouldReplaceCallerDetailsWithSafeText(PatientImportRejection reason, string expected)
    {
        const string privateDetails = "JWT secret-token; SQL password=private";

        var acknowledgement = Hl7AcknowledgementFactory.Rejected(new PatientImportRejected(reason, privateDetails));
        var actual = Text(_encoder.Encode(Header(), acknowledgement));

        acknowledgement.Code.Should().Be(Hl7AcknowledgementCode.AE);
        acknowledgement.Text.Should().Be(expected);
        actual.Should().Contain("MSA|AE|request-001|" + expected + "\r");
        actual.Should().NotContain(privateDetails).And.NotContain("secret-token").And.NotContain("password");
    }

    [Fact]
    public void InternalFailure_ShouldUseFixedSafeText()
    {
        var actual = Text(_encoder.Encode(Header(), Hl7AcknowledgementFactory.InternalFailure()));

        actual.Should().EndWith("MSA|AE|request-001|INTERNAL: импорт не выполнен\r");
    }

    [Theory]
    [InlineData(Hl7AcknowledgementCode.AA, "CONNECTED", null)]
    [InlineData(Hl7AcknowledgementCode.AA, "00000000-0000-0000-0000-000000000000", null)]
    [InlineData(Hl7AcknowledgementCode.AA, "not-a-patient-id", null)]
    [InlineData(Hl7AcknowledgementCode.AE, "SQL secret", null)]
    [InlineData(Hl7AcknowledgementCode.AR, "AUTH: доступ запрещён", null)]
    [InlineData(Hl7AcknowledgementCode.AR, "AUTH: доступ запрещён", "SQL secret")]
    [InlineData(Hl7AcknowledgementCode.AR, "JWT secret", "207")]
    [InlineData((Hl7AcknowledgementCode)99, "SQL secret", null)]
    public void Encode_UnsafeOrInconsistentAcknowledgement_ShouldRejectWithoutEchoingDetails(
        Hl7AcknowledgementCode code, string text, string? errorCode)
    {
        var action = () => _encoder.Encode(Header(), new Hl7Acknowledgement(code, text, errorCode));

        var exception = action.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().NotContain("SQL secret").And.NotContain("JWT secret");
    }

    [Theory]
    [InlineData("ADT", "A28", "ADT_A05")]
    [InlineData("NMD", "N02", "NMD_N02")]
    [InlineData("ADT", "A08", "")]
    public void Encode_InvalidHeader_ShouldRejectControlInjection(string type, string trigger, string structure)
    {
        var request = Header() with
        {
            MessageType = type, TriggerEvent = trigger, MessageStructure = structure,
            ControlId = "request\rMSA|AA|injected"
        };
        var action = () => _encoder.Encode(request, Hl7AcknowledgementFactory.ProtocolFailure());

        action.Should().Throw<ArgumentException>().WithMessage("Некорректный заголовок*");
    }

    [Fact]
    public void Imported_EmptyPatientId_ShouldRejectInvalidCommitResult()
    {
        var action = () => Hl7AcknowledgementFactory.Imported(new PatientImported(Guid.Empty, 0));

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encode_PatientSuccessForProbe_ShouldRejectWrongResultKind()
    {
        var request = Header() with { MessageType = "NMD", TriggerEvent = "N02", MessageStructure = "NMD_N02" };
        var action = () => _encoder.Encode(request, Hl7AcknowledgementFactory.Imported(new PatientImported(PatientId, 0)));

        action.Should().Throw<ArgumentException>();
    }

    private static string Text(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);

    private static Hl7MessageHeader Header() => new()
    {
        MessageType = "ADT", TriggerEvent = "A28", MessageStructure = "ADT_A05", ControlId = "request-001",
        SendingApplication = "MEDSOFT_IMPORT", SendingFacility = "LAB4_XML", ReceivingApplication = "HIS",
        ReceivingFacility = "MEDSOFT", SentAt = Now
    };

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
