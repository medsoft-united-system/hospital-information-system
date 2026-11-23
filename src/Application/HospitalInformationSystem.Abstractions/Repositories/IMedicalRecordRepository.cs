using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Abstractions.Repositories;

public interface IMedicalRecordRepository
{
    Task<MedicalRecord?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    Task<MedicalRecord?> FindByPatientAsync(Guid patientId, CancellationToken cancellationToken);
    Task<Guid?> FindRecordIdByLaboratoryAnalysisRequestAsync(Guid requestId, CancellationToken cancellationToken);
    void Add(MedicalRecord record);
    Task UpdateAsync(MedicalRecord record, CancellationToken cancellationToken);
}
