using HospitalInformationSystem.Application.Contracts.Hl7Import;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using MediatR;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7MessageProcessor(
    IHl7MessageCodec codec,
    IHl7ImportTokenValidator tokenValidator,
    ISender sender,
    TimeProvider timeProvider,
    IHl7ImportFailureAuditor failureAuditor)
{
    public async Task<byte[]?> ProcessAsync(ReadOnlyMemory<byte> payload,
        ImportClientCertificate verifiedClientCertificate, string? remoteAddress,
        CancellationToken cancellationToken)
    {
        Hl7MessageHeader? header = null;
        ServiceAccountIdentity? identity = null;
        ImportedPatientInput? patient = null;
        var auditHandled = false;

        async Task RecordFailureOnceAsync(string outcome, string? externalPatientId = null)
        {
            if (auditHandled || header is null) return;
            auditHandled = true;
            await failureAuditor.RecordAsync(new Hl7ImportFailure(header.ControlId, outcome,
                verifiedClientCertificate, identity,
                header.SendingFacility == Hl7ImportProtocol.SourceSystem ? Hl7ImportProtocol.SourceSystem : null,
                identity is null ? null : externalPatientId), cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receivedAt = timeProvider.GetUtcNow().ToUniversalTime();
            var envelope = codec.DecodeEnvelope(payload);
            header = envelope.Header;
            identity = await ValidateIdentityAsync(envelope.AccessToken, cancellationToken);
            if (identity is null)
            {
                await RecordFailureOnceAsync(Hl7ImportAuditOutcomes.AuthenticationRejected);
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.AuthenticationFailure());
            }

            var message = codec.Decode(payload);
            patient = message.Patient;
            if (header.MessageType == "NMD" && header.TriggerEvent == "N02" &&
                header.MessageStructure == "NMD_N02" && patient is null)
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.ProbeAccepted());
            if (header.MessageType != "ADT" || header.TriggerEvent != "A28" ||
                header.MessageStructure != "ADT_A05" || patient is null)
            {
                await RecordFailureOnceAsync(Hl7ImportAuditOutcomes.ProtocolRejected);
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.ProtocolFailure());
            }

            var context = new ImportConnectionContext(identity, verifiedClientCertificate,
                receivedAt, remoteAddress);
            var result = await sender.Send(new ImportPatientCommand(header.ControlId,
                patient, context), cancellationToken);
            auditHandled = result is PatientImported or PatientImportRejected;
            var acknowledgement = result switch
            {
                PatientImported imported => Hl7AcknowledgementFactory.Imported(imported),
                PatientImportRejected rejected => Hl7AcknowledgementFactory.Rejected(rejected),
                _ => Hl7AcknowledgementFactory.InternalFailure()
            };
            if (result is not PatientImported and not PatientImportRejected)
                await RecordFailureOnceAsync(Hl7ImportAuditOutcomes.InternalError, patient.ExternalPatientId);
            return codec.EncodeAcknowledgement(header, acknowledgement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Hl7DecodeException exception)
        {
            header = exception.Header ?? header;
            if (header is null) return null;
            var outcome = exception.Kind switch
            {
                Hl7FailureKind.Authentication => Hl7ImportAuditOutcomes.AuthenticationRejected,
                Hl7FailureKind.Validation => Hl7ImportAuditOutcomes.ValidationFailed,
                _ => Hl7ImportAuditOutcomes.ProtocolRejected
            };
            await RecordFailureOnceAsync(outcome, exception.ExternalPatientId);
            var acknowledgement = exception.Kind switch
            {
                Hl7FailureKind.Authentication => Hl7AcknowledgementFactory.AuthenticationFailure(),
                Hl7FailureKind.Validation => Hl7AcknowledgementFactory.ValidationFailure(),
                _ => Hl7AcknowledgementFactory.ProtocolFailure()
            };
            return codec.EncodeAcknowledgement(header, acknowledgement);
        }
        catch (ServiceAccountAccessDeniedException)
        {
            if (header is null) return null;
            await RecordFailureOnceAsync(Hl7ImportAuditOutcomes.AuthenticationRejected, patient?.ExternalPatientId);
            return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.AuthenticationFailure());
        }
        catch
        {
            if (header is null) return null;
            await RecordFailureOnceAsync(Hl7ImportAuditOutcomes.InternalError, patient?.ExternalPatientId);
            return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.InternalFailure());
        }
    }

    private async Task<ServiceAccountIdentity?> ValidateIdentityAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await tokenValidator.ValidateImportAccessAsync(accessToken, cancellationToken);
            return identity is { Role: ServiceAccountRole.Hl7Import, AuthenticationVersion: > 0 } &&
                   identity.Id != Guid.Empty ? identity : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
