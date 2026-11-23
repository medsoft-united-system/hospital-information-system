using HospitalInformationSystem.Models.Diagnoses;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Patients;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Abstractions.Hl7Import;

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
