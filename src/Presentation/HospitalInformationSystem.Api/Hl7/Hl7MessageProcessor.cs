using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Domain.ServiceAccounts;
using MediatR;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7MessageProcessor(
    IHl7MessageCodec codec,
    IHl7ImportTokenValidator tokenValidator,
    ISender sender,
    TimeProvider timeProvider)
{
    public async Task<byte[]?> ProcessAsync(ReadOnlyMemory<byte> payload,
        ImportClientCertificate verifiedClientCertificate, string? remoteAddress,
        CancellationToken cancellationToken)
    {
        Hl7MessageHeader? header = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receivedAt = timeProvider.GetUtcNow().ToUniversalTime();
            var envelope = codec.DecodeEnvelope(payload);
            header = envelope.Header;
            var identity = await ValidateIdentityAsync(envelope.AccessToken, cancellationToken);
            if (identity is null)
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.AuthenticationFailure());

            var message = codec.Decode(payload);
            if (header.MessageType == "NMD" && header.TriggerEvent == "N02" &&
                header.MessageStructure == "NMD_N02" && message.Patient is null)
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.ProbeAccepted());
            if (header.MessageType != "ADT" || header.TriggerEvent != "A28" ||
                header.MessageStructure != "ADT_A05" || message.Patient is null)
                return codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.ProtocolFailure());

            var context = new ImportConnectionContext(identity, verifiedClientCertificate,
                receivedAt, remoteAddress);
            var result = await sender.Send(new ImportPatientCommand(header.ControlId,
                message.Patient, context), cancellationToken);
            var acknowledgement = result switch
            {
                PatientImported imported => Hl7AcknowledgementFactory.Imported(imported),
                PatientImportRejected rejected => Hl7AcknowledgementFactory.Rejected(rejected),
                _ => Hl7AcknowledgementFactory.InternalFailure()
            };
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
            return header is null ? null :
                codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.AuthenticationFailure());
        }
        catch
        {
            return header is null ? null :
                codec.EncodeAcknowledgement(header, Hl7AcknowledgementFactory.InternalFailure());
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
