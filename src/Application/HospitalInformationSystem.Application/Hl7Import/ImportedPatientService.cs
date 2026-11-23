using HospitalInformationSystem.Abstractions.Hl7Import;
using HospitalInformationSystem.Abstractions.Transactions;
using HospitalInformationSystem.Application.Diagnoses.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Patients.Factories;
using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Diagnoses;
using HospitalInformationSystem.Models.Hl7Import;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed class ImportedPatientService(IImportedPatientRepository repository, ITransactionManager transactions,
    TimeProvider timeProvider, IAuditWriter audit, IAuditActorScope auditContext) : IImportedPatientService
{
    public async Task<ImportPatientResult> ImportAsync(ImportPatientRequest command, CancellationToken cancellationToken)
    {
        RequireContext(command);
        if (transactions.HasActiveTransaction)
            throw new InvalidOperationException("Для пациента требуется отдельная транзакция импорта.");

        AuditActor? actor = null;
        try
        {
            await using var transaction = await transactions.BeginAsync(cancellationToken);
            var identity = command.Context.ServiceAccount;
            var account = await repository.LockServiceAccountAsync(identity.Id, cancellationToken);
            if (account is null || !account.IsActive || account.Role != ServiceAccountRole.Hl7Import ||
                account.AuthenticationVersion != identity.AuthenticationVersion)
                throw new ServiceAccountAccessDeniedException();
            actor = new AuditActor(AuditActorKind.ServiceAccount, null, account.Name,
                command.MessageControlId, "hl7-import", account.Id);
            using var actorScope = auditContext.Begin(actor);
            RequireExternalIdentity(command.Patient);
            var input = command.Patient;
            var existing = await repository.FindPatientAsync(input.SourceSystem, input.ExternalPatientId,
                cancellationToken);
            if (existing is not null)
            {
                AppendAudit(command, actor, Hl7ImportAuditOutcomes.Duplicate, existing.Value, 0);
                await repository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PatientImportRejected(PatientImportRejection.Duplicate, "Пациент уже импортирован.");
            }

            var now = ServiceAccountRules.Timestamp(timeProvider.GetUtcNow());
            var patient = PatientFactory.Create(input.FirstName, input.LastName, input.MiddleName,
                input.BirthDate, input.Sex, null, now);
            var diagnoses = ValidateDiagnoses(input, now);
            var definitions = await ResolveDefinitionsAsync(diagnoses, now, cancellationToken);
            var record = MedicalRecordFactory.CreateForService(patient.Id, account.Id, account.Name, now);
            foreach (var diagnosis in input.Diagnoses)
            {
                var definition = definitions[diagnosis.Code.Trim()];
                MedicalRecordService.AddImportedDiagnosis(record, definition.Id, definition.Code,
                    diagnosis.Title.Trim(), diagnosis.CodingSystem, diagnosis.DiagnosedAt, diagnosis.DiagnosisType,
                    account.Id, account.Name, now);
            }
            repository.Add(patient, record, input.SourceSystem, input.ExternalPatientId, now);
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.Success, patient.Id, input.Diagnoses.Count);
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PatientImported(patient.Id, input.Diagnoses.Count);
        }
        catch (Exception exception) when (exception is DomainValidationException ||
            exception is PatientImportConflictException { Conflict: PatientImportConflict.DiagnosisName })
        {
            transactions.DiscardChanges();
            if (actor is null) throw;
            using var actorScope = auditContext.Begin(actor);
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.ValidationFailed, null, 0);
            await repository.SaveChangesAsync(cancellationToken);
            return new PatientImportRejected(PatientImportRejection.Validation,
                exception is PatientImportConflictException
                    ? "Название диагноза уже принадлежит другому коду. Пациент не импортирован."
                    : "Проверьте данные пациента и диагнозов. Пациент не импортирован.");
        }
        catch (PatientImportConflictException exception) when (exception.Conflict == PatientImportConflict.ExternalIdentity)
        {
            transactions.DiscardChanges();
            if (actor is null) throw;
            using var actorScope = auditContext.Begin(actor);
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.Duplicate, null, 0);
            await repository.SaveChangesAsync(cancellationToken);
            return new PatientImportRejected(PatientImportRejection.Duplicate, "Пациент уже импортирован.");
        }
        catch
        {
            transactions.DiscardChanges();
            throw;
        }
    }

    private static IReadOnlyList<DiagnosisDefinition> ValidateDiagnoses(ImportedPatientInput input, DateTimeOffset now)
    {
        if (input.Diagnoses is null || input.Diagnoses.Count > Hl7ImportProtocol.MaxDiagnoses)
            throw new DomainValidationException("Недопустимое количество диагнозов.");
        var definitions = new List<DiagnosisDefinition>();
        foreach (var diagnosis in input.Diagnoses)
        {
            if (diagnosis is null || diagnosis.CodingSystem != Hl7ImportProtocol.DiagnosisCodingSystem ||
                diagnosis.DiagnosisType != Hl7ImportProtocol.UnspecifiedDiagnosisType ||
                diagnosis.DiagnosedAt == default || diagnosis.DiagnosedAt > now ||
                DateOnly.FromDateTime(diagnosis.DiagnosedAt.DateTime) < input.BirthDate ||
                diagnosis.DiagnosedAt.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new DomainValidationException("Недопустимые клинические данные диагноза.");
            definitions.Add(DiagnosisDefinitionFactory.Create(diagnosis.Code, diagnosis.Title, now));
        }
        return definitions;
    }

    private async Task<Dictionary<string, DiagnosisDefinition>> ResolveDefinitionsAsync(
        IReadOnlyList<DiagnosisDefinition> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, DiagnosisDefinition>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(row => row.Code, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var existing = await repository.FindDiagnosisAsync(group.Key, cancellationToken);
            if (existing is null)
            {
                if (group.Select(row => row.Name).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new DomainValidationException("Для нового кода переданы разные названия.");
                existing = await repository.ResolveDiagnosisAsync(group.First(), now, cancellationToken);
            }
            definitions.Add(group.Key, existing);
        }
        return definitions;
    }

    private static void RequireContext(ImportPatientRequest command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var context = command.Context;
        if (context?.ServiceAccount is not { Role: ServiceAccountRole.Hl7Import, AuthenticationVersion: > 0 } identity ||
            identity.Id == Guid.Empty || context.ClientCertificate is not { } certificate ||
            certificate.Sha256Fingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit))
            throw new ServiceAccountAccessDeniedException();
        if (!SafeText(command.MessageControlId, 128))
            throw new DomainValidationException("Некорректный идентификатор сообщения.");
    }

    private static void RequireExternalIdentity(ImportedPatientInput? input)
    {
        if (input is null || input.SourceSystem != Hl7ImportProtocol.SourceSystem ||
            !SafeText(input.ExternalPatientId, 128) || input.ExternalPatientId != input.ExternalPatientId.Trim())
            throw new DomainValidationException("Некорректный внешний идентификатор пациента.");
    }

    private void AppendAudit(ImportPatientRequest command, AuditActor actor, string outcome, Guid? patientId, int count)
    {
        audit.Append(AuditEventType.Hl7PatientImported,
            outcome == Hl7ImportAuditOutcomes.Success ? "Пациент импортирован через HL7." :
                "Импорт пациента через HL7 отклонён.",
            Hl7ImportAuditMetadataFactory.Create(outcome, command.Context.ClientCertificate,
                actor.ServiceAccountId, command.Patient?.SourceSystem, command.Patient?.ExternalPatientId,
                patientId, count), actor);
    }

    private static bool SafeText(string? value, int max) => value is { Length: > 0 } && value.Length <= max &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

}
