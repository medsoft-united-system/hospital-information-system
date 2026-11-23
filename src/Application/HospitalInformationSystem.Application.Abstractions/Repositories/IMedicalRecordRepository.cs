using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Abstractions.Repositories;

public interface IMedicalRecordRepository
{
    Task<MedicalRecord?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    Task<MedicalRecord?> FindByPatientAsync(Guid patientId, CancellationToken cancellationToken);
    Task<Guid?> FindRecordIdByLaboratoryAnalysisRequestAsync(Guid requestId, CancellationToken cancellationToken);
    void Add(MedicalRecord record);
    Task UpdateAsync(MedicalRecord record, CancellationToken cancellationToken);
}
