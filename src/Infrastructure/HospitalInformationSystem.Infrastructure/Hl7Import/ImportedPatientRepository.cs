using HospitalInformationSystem.Application.Abstractions.Hl7Import;
using HospitalInformationSystem.Application.Models.Diagnoses;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalInformationSystem.Infrastructure.Hl7Import;

public sealed class ImportedPatientRepository(HospitalDbContext db) : IImportedPatientRepository
{
    public async Task<ServiceAccount?> LockServiceAccountAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.ServiceAccounts.FromSqlInterpolated(
            $"SELECT *, xmin FROM service_accounts WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<Guid?> FindPatientAsync(string sourceSystem, string externalPatientId,
        CancellationToken cancellationToken)
    {
        var lockKey = "hl7-patient:" + sourceSystem + ":" + externalPatientId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        return await db.PatientExternalIdentifiers.AsNoTracking()
            .Where(row => row.SourceSystem == sourceSystem && row.ExternalPatientId == externalPatientId)
            .Select(row => (Guid?)row.PatientId).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<DiagnosisDefinition?> FindDiagnosisAsync(string code, CancellationToken cancellationToken)
    {
        var row = await db.DiagnosisDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Code == code, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<DiagnosisDefinition> ResolveDiagnosisAsync(DiagnosisDefinition candidate, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO diagnosis_definitions (id, code, name, created_at_utc, updated_at_utc)
                VALUES ({candidate.Id}, {candidate.Code}, {candidate.Name}, {now}, {now})
                ON CONFLICT (code) DO NOTHING
                """, cancellationToken);
            var row = await db.DiagnosisDefinitions.AsNoTracking()
                .SingleAsync(row => row.Code == candidate.Code, cancellationToken);
            return row.ToDomain();
        }
        catch (Exception exception) when (IsConflict(exception, "ux_diagnosis_definitions_name"))
        {
            throw new PatientImportConflictException(PatientImportConflict.DiagnosisName, exception);
        }
    }

    public void Add(Patient patient, MedicalRecord record, string sourceSystem, string externalPatientId,
        DateTimeOffset now)
    {
        db.Patients.Add(patient.ToDbo());
        db.MedicalRecords.Add(record.ToDbo());
        db.PatientExternalIdentifiers.Add(new PatientExternalIdentifierDbo
        {
            SourceSystem = sourceSystem,
            ExternalPatientId = externalPatientId,
            PatientId = patient.Id,
            CreatedAtUtc = now
        });
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (Exception exception) when (IsConflict(exception, "ux_diagnosis_definitions_name"))
        {
            throw new PatientImportConflictException(PatientImportConflict.DiagnosisName, exception);
        }
        catch (Exception exception) when (IsConflict(exception, "pk_patient_external_identifiers"))
        {
            throw new PatientImportConflictException(PatientImportConflict.ExternalIdentity, exception);
        }
    }

    private static bool IsConflict(Exception exception, string constraint) =>
        exception.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres &&
        postgres.ConstraintName == constraint;
}
