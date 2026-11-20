using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.Diagnoses;
using HospitalInformationSystem.Domain.Diagnoses.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Domain.Patients.Factories;
using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalInformationSystem.Infrastructure.Hl7Import;

public sealed class ImportedPatientService(HospitalDbContext db, TimeProvider timeProvider,
    IAuditWriter audit, ImportAuditContext auditContext) : IImportedPatientService
{
    public async Task<ImportPatientResult> ImportAsync(ImportPatientCommand command, CancellationToken cancellationToken)
    {
        RequireContext(command);
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Для пациента требуется отдельная транзакция импорта.");

        AuditActor? actor = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var identity = command.Context.ServiceAccount;
            var account = await db.ServiceAccounts.FromSqlInterpolated(
                    $"SELECT *, xmin FROM service_accounts WHERE id = {identity.Id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (account is null || !account.IsActive || account.Role != ServiceAccountRole.Hl7Import ||
                account.AuthenticationVersion != identity.AuthenticationVersion)
                throw new ServiceAccountAccessDeniedException();
            actor = new AuditActor(AuditActorKind.ServiceAccount, null, account.Name,
                command.MessageControlId, "hl7-import", account.Id);
            using var actorScope = auditContext.Begin(actor);
            RequireExternalIdentity(command.Patient);
            var input = command.Patient;
            var lockKey = "hl7-patient:" + input.SourceSystem + ":" + input.ExternalPatientId;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
            var existing = await db.PatientExternalIdentifiers.AsNoTracking().SingleOrDefaultAsync(row =>
                row.SourceSystem == input.SourceSystem && row.ExternalPatientId == input.ExternalPatientId,
                cancellationToken);
            if (existing is not null)
            {
                AppendAudit(command, actor, Hl7ImportAuditOutcomes.Duplicate, existing.PatientId, 0);
                await db.SaveChangesAsync(cancellationToken);
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
            db.Patients.Add(patient.ToDbo());
            db.MedicalRecords.Add(record.ToDbo());
            db.PatientExternalIdentifiers.Add(new PatientExternalIdentifierDbo
            {
                SourceSystem = input.SourceSystem, ExternalPatientId = input.ExternalPatientId,
                PatientId = patient.Id, CreatedAtUtc = now
            });
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.Success, patient.Id, input.Diagnoses.Count);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PatientImported(patient.Id, input.Diagnoses.Count);
        }
        catch (Exception exception) when (exception is DomainValidationException || IsNameConflict(exception))
        {
            db.ChangeTracker.Clear();
            if (actor is null) throw;
            using var actorScope = auditContext.Begin(actor);
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.ValidationFailed, null, 0);
            await db.SaveChangesAsync(cancellationToken);
            return new PatientImportRejected(PatientImportRejection.Validation,
                IsNameConflict(exception)
                    ? "Название диагноза уже принадлежит другому коду. Пациент не импортирован."
                    : "Проверьте данные пациента и диагнозов. Пациент не импортирован.");
        }
        catch (Exception exception) when (IsExternalIdentityConflict(exception))
        {
            db.ChangeTracker.Clear();
            if (actor is null) throw;
            using var actorScope = auditContext.Begin(actor);
            AppendAudit(command, actor, Hl7ImportAuditOutcomes.Duplicate, null, 0);
            await db.SaveChangesAsync(cancellationToken);
            return new PatientImportRejected(PatientImportRejection.Duplicate, "Пациент уже импортирован.");
        }
        catch
        {
            db.ChangeTracker.Clear();
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

    private async Task<Dictionary<string, DiagnosisDefinitionDbo>> ResolveDefinitionsAsync(
        IReadOnlyList<DiagnosisDefinition> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, DiagnosisDefinitionDbo>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(row => row.Code, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var existing = await db.DiagnosisDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Code == group.Key, cancellationToken);
            if (existing is null)
            {
                if (group.Select(row => row.Name).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new DomainValidationException("Для нового кода переданы разные названия.");
                var candidate = group.First();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO diagnosis_definitions (id, code, name, created_at_utc, updated_at_utc)
                    VALUES ({candidate.Id}, {candidate.Code}, {candidate.Name}, {now}, {now})
                    ON CONFLICT (code) DO NOTHING
                    """, cancellationToken);
                existing = await db.DiagnosisDefinitions.AsNoTracking()
                    .SingleAsync(row => row.Code == group.Key, cancellationToken);
            }
            definitions.Add(group.Key, existing);
        }
        return definitions;
    }

    private static void RequireContext(ImportPatientCommand command)
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

    private void AppendAudit(ImportPatientCommand command, AuditActor actor, string outcome, Guid? patientId, int count)
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

    private static bool IsNameConflict(Exception exception) => exception.GetBaseException() is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_diagnosis_definitions_name" };

    private static bool IsExternalIdentityConflict(Exception exception) => exception.GetBaseException() is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_patient_external_identifiers" };
}
