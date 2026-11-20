using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Domain.Patients;
using HospitalInformationSystem.Domain.ServiceAccounts;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7MessageProcessorTests
{
    private const string AccessToken = "sensitive-test-access-token";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 34, 56, TimeSpan.Zero);
    private static readonly ReadOnlyMemory<byte> Payload = Encoding.UTF8.GetBytes("payload");
    private static readonly ImportClientCertificate Certificate = new(new string('A', 64),
        "CN=desktop", "CN=HIS CA", "12AB", Now.AddDays(-1), Now.AddDays(1));
    private static readonly ServiceAccountIdentity Identity = new(Guid.NewGuid(), "hl7-import", 3,
        ServiceAccountRole.Hl7Import);
    private readonly Mock<IHl7MessageCodec> _codec = new(MockBehavior.Strict);
    private readonly Mock<IHl7ImportTokenValidator> _tokens = new(MockBehavior.Strict);
    private readonly Mock<ISender> _sender = new(MockBehavior.Strict);
    private readonly Mock<IHl7ImportFailureAuditor> _failureAuditor = new();
    private readonly Mock<TimeProvider> _clock = new(MockBehavior.Strict);
    private Hl7Acknowledgement? _acknowledgement;
    private Hl7MessageHeader? _responseHeader;

    public Lab4Hl7MessageProcessorTests()
    {
        _clock.Setup(clock => clock.GetUtcNow()).Returns(Now);
        _codec.Setup(codec => codec.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
                It.IsAny<Hl7Acknowledgement>()))
            .Returns((Hl7MessageHeader header, Hl7Acknowledgement acknowledgement) =>
            {
                _responseHeader = header;
                _acknowledgement = acknowledgement;
                return Encoding.UTF8.GetBytes(acknowledgement.Code + "|" + acknowledgement.Text);
            });
    }

    [Fact]
    public async Task Patient_ShouldValidateCredentialBeforeMappingAndPassVerifiedContextToCommand()
    {
        var header = Header();
        var patient = Patient();
        var result = new PatientImported(Guid.NewGuid(), 0);
        var order = new MockSequence();
        _codec.InSequence(order).Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(header));
        _tokens.InSequence(order).Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken,
            CancellationToken.None)).ReturnsAsync(Identity);
        _codec.InSequence(order).Setup(codec => codec.Decode(Payload)).Returns(Message(header, patient));
        ImportPatientCommand? captured = null;
        _sender.InSequence(order).Setup(sender => sender.Send<ImportPatientResult>(
                It.IsAny<ImportPatientCommand>(), CancellationToken.None))
            .Callback<IRequest<ImportPatientResult>, CancellationToken>((request, _) =>
                captured = (ImportPatientCommand)request)
            .ReturnsAsync(result);

        var response = await Processor().ProcessAsync(Payload, Certificate, "192.0.2.1",
            CancellationToken.None);

        response.Should().NotBeNull();
        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.Imported(result));
        _responseHeader.Should().Be(header);
        captured.Should().NotBeNull();
        captured!.MessageControlId.Should().Be(header.ControlId);
        captured.Patient.Should().BeSameAs(patient);
        captured.Context.Should().Be(new ImportConnectionContext(Identity, Certificate, Now, "192.0.2.1"));
    }

    [Fact]
    public async Task Probe_ShouldValidateAuthenticationAndReturnConnectedWithoutImport()
    {
        var header = Header(probe: true);
        SetMessage(header, null);

        await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.ProbeAccepted());
        _tokens.Verify(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None), Times.Once);
        _codec.Verify(codec => codec.Decode(Payload), Times.Once);
        _sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("lis")]
    [InlineData("unknown-role")]
    [InlineData("empty-id")]
    [InlineData("invalid-version")]
    public async Task InvalidIdentity_ShouldRefuseBeforeClinicalMappingOrImport(string reason)
    {
        var header = Header();
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(header));
        var identity = reason switch
        {
            "missing" => null,
            "lis" => Identity with { Role = ServiceAccountRole.Lis },
            "unknown-role" => Identity with { Role = (ServiceAccountRole)999 },
            "empty-id" => Identity with { Id = Guid.Empty },
            "invalid-version" => Identity with { AuthenticationVersion = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None))
            .ReturnsAsync(identity);

        await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.AuthenticationFailure());
        _codec.Verify(codec => codec.Decode(It.IsAny<ReadOnlyMemory<byte>>()), Times.Never);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthenticationException_ShouldReturnSafeRefusalWithoutMapping()
    {
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(Header()));
        _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("SQL credentials: " + AccessToken));

        var response = await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.AuthenticationFailure());
        Encoding.UTF8.GetString(response!).Should().NotContain(AccessToken).And.NotContain("SQL");
        _codec.Verify(codec => codec.Decode(It.IsAny<ReadOnlyMemory<byte>>()), Times.Never);
        _sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(Hl7FailureKind.Protocol)]
    [InlineData(Hl7FailureKind.Authentication)]
    [InlineData(Hl7FailureKind.Validation)]
    public async Task DecodeFailureWithHeader_ShouldReturnCorrelatedSafeAcknowledgement(Hl7FailureKind kind)
    {
        var header = Header();
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Throws(new Hl7DecodeException(kind, header));

        await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _responseHeader.Should().Be(header);
        _acknowledgement.Should().Be(kind switch
        {
            Hl7FailureKind.Authentication => Hl7AcknowledgementFactory.AuthenticationFailure(),
            Hl7FailureKind.Validation => Hl7AcknowledgementFactory.ValidationFailure(),
            _ => Hl7AcknowledgementFactory.ProtocolFailure()
        });
        _tokens.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecodeFailureWithoutReadableHeader_ShouldReturnNoAcknowledgement()
    {
        _codec.Setup(codec => codec.DecodeEnvelope(Payload))
            .Throws(new Hl7DecodeException(Hl7FailureKind.Protocol));

        var response = await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        response.Should().BeNull();
        _codec.Verify(codec => codec.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
            It.IsAny<Hl7Acknowledgement>()), Times.Never);
        _tokens.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClinicalDecodeFailure_ShouldBeMappedAfterAuthenticationAndBeforeImport()
    {
        var header = Header();
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(header));
        _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None))
            .ReturnsAsync(Identity);
        _codec.Setup(codec => codec.Decode(Payload))
            .Throws(new Hl7DecodeException(Hl7FailureKind.Validation, header));

        await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.ValidationFailure());
        _tokens.Verify(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None), Times.Once);
        _sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PatientImportRejection.Duplicate)]
    [InlineData(PatientImportRejection.Validation)]
    public async Task ImportRejection_ShouldUseFixedSafeText(PatientImportRejection reason)
    {
        SetMessage(Header(), Patient());
        var rejection = new PatientImportRejected(reason, "SQL: " + AccessToken);
        _sender.Setup(sender => sender.Send<ImportPatientResult>(It.IsAny<ImportPatientCommand>(),
            CancellationToken.None)).ReturnsAsync(rejection);

        var response = await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.Rejected(rejection));
        Encoding.UTF8.GetString(response!).Should().NotContain(AccessToken).And.NotContain("SQL");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportException_ShouldRefuseWithoutLeakingExceptionDetails(bool accessDenied)
    {
        SetMessage(Header(), Patient());
        Exception exception = accessDenied ? new ServiceAccountAccessDeniedException() :
            new InvalidOperationException("SQL: " + AccessToken);
        _sender.Setup(sender => sender.Send<ImportPatientResult>(It.IsAny<ImportPatientCommand>(),
            CancellationToken.None)).ThrowsAsync(exception);

        var response = await Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        _acknowledgement.Should().Be(accessDenied ? Hl7AcknowledgementFactory.AuthenticationFailure() :
            Hl7AcknowledgementFactory.InternalFailure());
        Encoding.UTF8.GetString(response!).Should().NotContain(AccessToken).And.NotContain("SQL");
    }

    [Fact]
    public async Task PendingImport_ShouldNotEmitSuccessBeforeCommandCompletes()
    {
        SetMessage(Header(), Patient());
        var completion = new TaskCompletionSource<ImportPatientResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Setup(sender => sender.Send<ImportPatientResult>(It.IsAny<ImportPatientCommand>(),
            CancellationToken.None)).Returns(completion.Task);

        var processing = Processor().ProcessAsync(Payload, Certificate, null, CancellationToken.None);

        processing.IsCompleted.Should().BeFalse();
        _codec.Verify(codec => codec.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
            It.IsAny<Hl7Acknowledgement>()), Times.Never);
        var result = new PatientImported(Guid.NewGuid(), 0);
        completion.SetResult(result);
        await processing;
        _acknowledgement.Should().Be(Hl7AcknowledgementFactory.Imported(result));
    }

    [Fact]
    public async Task RequestedCancellation_ShouldPropagateWithoutAcknowledgement()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var process = () => Processor().ProcessAsync(Payload, Certificate, null, cancellation.Token);

        await process.Should().ThrowAsync<OperationCanceledException>();
        _codec.VerifyNoOtherCalls();
        _tokens.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("import")]
    public async Task CancellationDuringProcessing_ShouldPropagateWithoutAcknowledgement(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var header = Header();
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(header));
        if (phase == "authentication")
        {
            _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, cancellation.Token))
                .Returns(() =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<ServiceAccountIdentity?>(cancellation.Token);
                });
        }
        else
        {
            _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, cancellation.Token)).ReturnsAsync(Identity);
            _codec.Setup(codec => codec.Decode(Payload)).Returns(Message(header, Patient()));
            _sender.Setup(sender => sender.Send<ImportPatientResult>(It.IsAny<ImportPatientCommand>(), cancellation.Token))
                .Returns(() =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<ImportPatientResult>(cancellation.Token);
                });
        }

        var process = () => Processor().ProcessAsync(Payload, Certificate, null, cancellation.Token);

        await process.Should().ThrowAsync<OperationCanceledException>();
        _codec.Verify(codec => codec.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
            It.IsAny<Hl7Acknowledgement>()), Times.Never);
        if (phase == "authentication") _sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ADT^A28^ADT_A05^UNSUPPORTED")]
    [InlineData("ADT^A28^ADT_A05&UNSUPPORTED")]
    public async Task UnsupportedMessageStructure_ShouldReturnCorrelatedProtocolAck(string messageType)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"MSH|^~\\&|MEDSOFT_IMPORT|LAB4_XML|HIS|MEDSOFT|20261117123456+0000||{messageType}|correlation-001|P|2.8||||||UNICODE UTF-8\r");
        var processor = new Hl7MessageProcessor(new Hl7MessageCodec(_clock.Object),
            _tokens.Object, _sender.Object, _clock.Object, _failureAuditor.Object);

        var response = await processor.ProcessAsync(payload, Certificate, null, CancellationToken.None);

        response.Should().NotBeNull();
        var segments = Encoding.UTF8.GetString(response!).Split('\r', StringSplitOptions.RemoveEmptyEntries);
        segments[0].Split('|')[8].Should().Be("ACK^A28^ACK");
        var msa = segments.Single(segment => segment.StartsWith("MSA|", StringComparison.Ordinal)).Split('|');
        msa[1].Should().Be("AR");
        msa[2].Should().Be("correlation-001");
        msa[3].Should().StartWith("PROTOCOL:");
        _tokens.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public void Registration_ShouldResolveScopedProcessorAndSingletonCodec()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_clock.Object);
        services.AddSingleton(_tokens.Object);
        services.AddSingleton(_sender.Object);
        services.AddSingleton(_failureAuditor.Object);
        services.AddHl7ImportCodec();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<IHl7MessageCodec>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IHl7MessageCodec>());
        first.ServiceProvider.GetRequiredService<Hl7MessageProcessor>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<Hl7MessageProcessor>());
    }

    private Hl7MessageProcessor Processor() => new(_codec.Object, _tokens.Object, _sender.Object, _clock.Object, _failureAuditor.Object);

    private void SetMessage(Hl7MessageHeader header, ImportedPatientInput? patient)
    {
        _codec.Setup(codec => codec.DecodeEnvelope(Payload)).Returns(Envelope(header));
        _tokens.Setup(tokens => tokens.ValidateImportAccessAsync(AccessToken, CancellationToken.None)).ReturnsAsync(Identity);
        _codec.Setup(codec => codec.Decode(Payload)).Returns(Message(header, patient));
    }

    private static Hl7MessageEnvelope Envelope(Hl7MessageHeader header) => new(AccessToken) { Header = header };
    private static Hl7IncomingMessage Message(Hl7MessageHeader header, ImportedPatientInput? patient) =>
        new(AccessToken) { Header = header, Patient = patient };

    private static Hl7MessageHeader Header(bool probe = false) => new()
    {
        MessageType = probe ? "NMD" : "ADT", TriggerEvent = probe ? "N02" : "A28",
        MessageStructure = probe ? "NMD_N02" : "ADT_A05", ControlId = "message-001",
        SendingApplication = "MEDSOFT_IMPORT", SendingFacility = "LAB4_XML",
        ReceivingApplication = "HIS", ReceivingFacility = "MEDSOFT", SentAt = Now
    };

    private static ImportedPatientInput Patient() => new()
    {
        SourceSystem = "LAB4_XML", ExternalPatientId = "000042", FirstName = "Иван", LastName = "Иванов",
        BirthDate = new DateOnly(2000, 1, 2), Sex = AdministrativeSex.Male, Diagnoses = []
    };
}
