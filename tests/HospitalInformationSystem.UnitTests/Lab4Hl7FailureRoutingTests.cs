using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Domain.ServiceAccounts;
using MediatR;
using Moq;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4Hl7FailureRoutingTests
{
    private const string Token = "lab4-fixture-access-token-not-valid";
    private static readonly ImportClientCertificate Certificate = new(new string('A', 64),
        "CN=trusted desktop", "CN=test CA", "42", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    private static readonly ServiceAccountIdentity Identity = new(Guid.NewGuid(), "hl7.verified", 1,
        ServiceAccountRole.Hl7Import);

    [Fact]
    public async Task ClinicalDecodeFailure_ShouldAuditVerifiedServiceAndSafeExternalId()
    {
        var tokens = new Mock<IHl7ImportTokenValidator>();
        tokens.Setup(value => value.ValidateImportAccessAsync(Token, CancellationToken.None)).ReturnsAsync(Identity);
        var audit = new Mock<IHl7ImportFailureAuditor>();
        var sender = new Mock<ISender>(MockBehavior.Strict);
        var processor = new Hl7MessageProcessor(new Hl7MessageCodec(TimeProvider.System),
            tokens.Object, sender.Object, TimeProvider.System, audit.Object);

        await processor.ProcessAsync(Fixture("adt-invalid-birth-date.hl7"), Certificate, null, CancellationToken.None);

        audit.Verify(value => value.RecordAsync(It.Is<Hl7ImportFailure>(failure =>
            failure.Outcome == Hl7ImportAuditOutcomes.ValidationFailed &&
            failure.ServiceAccount == Identity && failure.ClientCertificate == Certificate &&
            failure.SourceSystem == "LAB4_XML" && failure.ExternalPatientId == "000043"),
            CancellationToken.None), Times.Once);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnauthorizedMessage_ShouldAuditCertificateWithoutClaimedClinicalIdentity()
    {
        var tokens = new Mock<IHl7ImportTokenValidator>();
        var audit = new Mock<IHl7ImportFailureAuditor>();
        Hl7ImportFailure? captured = null;
        audit.Setup(value => value.RecordAsync(It.IsAny<Hl7ImportFailure>(), CancellationToken.None))
            .Callback<Hl7ImportFailure, CancellationToken>((failure, _) => captured = failure).ReturnsAsync(false);
        var sender = new Mock<ISender>(MockBehavior.Strict);
        var processor = new Hl7MessageProcessor(new Hl7MessageCodec(TimeProvider.System),
            tokens.Object, sender.Object, TimeProvider.System, audit.Object);

        var response = await processor.ProcessAsync(Fixture("adt-multiple-diagnoses.hl7"),
            Certificate, null, CancellationToken.None);

        Encoding.UTF8.GetString(response!).Should().Contain("AUTH:");
        captured.Should().NotBeNull();
        captured!.ServiceAccount.Should().BeNull();
        captured.ExternalPatientId.Should().BeNull();
        captured.ClientCertificate.Should().Be(Certificate);
        JsonSerializer.Serialize(captured).Should().NotContain(Token).And.NotContain("Кузнецов");
        sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("duplicate")]
    [InlineData("validation")]
    public async Task ReturnedImportResult_ShouldNotCreateAdditionalRefusalAfterAckFailure(string outcome)
    {
        var codec = new Mock<IHl7MessageCodec>();
        var real = new Hl7MessageCodec(TimeProvider.System);
        ReadOnlyMemory<byte> payload = Fixture("adt-no-diagnoses.hl7");
        var envelope = real.DecodeEnvelope(payload);
        var message = real.Decode(payload);
        codec.Setup(value => value.DecodeEnvelope(payload)).Returns(envelope);
        codec.Setup(value => value.Decode(payload)).Returns(message);
        var tokens = new Mock<IHl7ImportTokenValidator>();
        tokens.Setup(value => value.ValidateImportAccessAsync(Token, CancellationToken.None)).ReturnsAsync(Identity);
        var sender = new Mock<ISender>();
        ImportPatientResult result = outcome switch
        {
            "success" => new PatientImported(Guid.NewGuid(), 0),
            "duplicate" => new PatientImportRejected(PatientImportRejection.Duplicate, "safe"),
            _ => new PatientImportRejected(PatientImportRejection.Validation, "safe")
        };
        sender.Setup(value => value.Send<ImportPatientResult>(It.IsAny<ImportPatientCommand>(), CancellationToken.None))
            .ReturnsAsync(result);
        codec.SetupSequence(value => value.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
                It.IsAny<Hl7Acknowledgement>()))
            .Throws(new InvalidOperationException("private-ack-error"))
            .Returns(Encoding.UTF8.GetBytes("safe-fallback"));
        var audit = new Mock<IHl7ImportFailureAuditor>(MockBehavior.Strict);
        var processor = new Hl7MessageProcessor(codec.Object, tokens.Object, sender.Object, TimeProvider.System, audit.Object);

        var response = await processor.ProcessAsync(payload, Certificate, null, CancellationToken.None);

        Encoding.UTF8.GetString(response!).Should().Be("safe-fallback");
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefusalAlreadyAudited_ShouldNotBeAuditedAgainAfterAckFailure()
    {
        var real = new Hl7MessageCodec(TimeProvider.System);
        ReadOnlyMemory<byte> payload = Fixture("adt-no-diagnoses.hl7");
        var codec = new Mock<IHl7MessageCodec>();
        codec.Setup(value => value.DecodeEnvelope(payload)).Returns(real.DecodeEnvelope(payload));
        codec.SetupSequence(value => value.EncodeAcknowledgement(It.IsAny<Hl7MessageHeader>(),
                It.IsAny<Hl7Acknowledgement>()))
            .Throws(new InvalidOperationException("private-ack-error"))
            .Returns(Encoding.UTF8.GetBytes("safe-fallback"));
        var audit = new Mock<IHl7ImportFailureAuditor>();
        var processor = new Hl7MessageProcessor(codec.Object, new Mock<IHl7ImportTokenValidator>().Object,
            new Mock<ISender>(MockBehavior.Strict).Object, TimeProvider.System, audit.Object);

        await processor.ProcessAsync(payload, Certificate, null, CancellationToken.None);

        audit.Verify(value => value.RecordAsync(It.IsAny<Hl7ImportFailure>(), CancellationToken.None), Times.Once);
    }

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", name));
}
