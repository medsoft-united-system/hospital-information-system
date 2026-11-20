using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.ServiceAccounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.Infrastructure.Hl7Import;

public sealed class Hl7ImportFailureAuditor(
    IServiceScopeFactory scopeFactory,
    ILogger<Hl7ImportFailureAuditor> logger) : IHl7ImportFailureAuditor
{
    public async Task<bool> RecordAsync(Hl7ImportFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (failure.Outcome is not (Hl7ImportAuditOutcomes.AuthenticationRejected or
                Hl7ImportAuditOutcomes.ProtocolRejected or Hl7ImportAuditOutcomes.ValidationFailed or
                Hl7ImportAuditOutcomes.InternalError))
                throw new DomainValidationException("Для отказа указан недопустимый результат импорта.");
            if (string.IsNullOrWhiteSpace(failure.MessageControlId) || failure.MessageControlId.Length > 128 ||
                failure.MessageControlId.Any(char.IsControl))
                throw new DomainValidationException("Для отказа указан недопустимый идентификатор сообщения.");

            var actor = CreateActor(failure.ServiceAccount, failure.MessageControlId);
            var metadata = Hl7ImportAuditMetadataFactory.Create(failure.Outcome,
                failure.ClientCertificate, actor.ServiceAccountId, failure.SourceSystem,
                actor.Kind == AuditActorKind.ServiceAccount ? failure.ExternalPatientId : null, null, 0);
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IAuditWriter>().Append(
                AuditEventType.Hl7PatientImported, "Импорт пациента через HL7 отклонён.", metadata, actor);
            var saved = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .SaveChangesAsync(cancellationToken);
            if (saved > 0) return true;
            logger.LogError("HL7 import refusal audit was not persisted.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogError("HL7 import refusal audit could not be persisted.");
            return false;
        }
    }

    private static AuditActor CreateActor(ServiceAccountIdentity? identity, string requestId)
    {
        if (identity is { Role: ServiceAccountRole.Hl7Import, AuthenticationVersion: > 0 } &&
            identity.Id != Guid.Empty && ServiceAccountRules.IsValidName(identity.Name))
            return new AuditActor(AuditActorKind.ServiceAccount, null,
                ServiceAccountRules.NormalizeName(identity.Name), requestId, "hl7-import", identity.Id);
        return new AuditActor(AuditActorKind.Anonymous, null,
            "Неаутентифицированный запрос HL7", requestId, "hl7-import");
    }
}
