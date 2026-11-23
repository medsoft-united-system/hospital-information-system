using HospitalInformationSystem.Application.Models.Diagnoses;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Abstractions.Hl7Import;

public interface IImportedPatientRepository
{
    Task<ServiceAccount?> LockServiceAccountAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid?> FindPatientAsync(string sourceSystem, string externalPatientId, CancellationToken cancellationToken);
    Task<DiagnosisDefinition?> FindDiagnosisAsync(string code, CancellationToken cancellationToken);
    Task<DiagnosisDefinition> ResolveDiagnosisAsync(DiagnosisDefinition candidate, DateTimeOffset now,
        CancellationToken cancellationToken);
    void Add(Patient patient, MedicalRecord record, string sourceSystem, string externalPatientId, DateTimeOffset now);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
