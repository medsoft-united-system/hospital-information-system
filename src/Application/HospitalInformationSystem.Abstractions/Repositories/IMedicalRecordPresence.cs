namespace HospitalInformationSystem.Abstractions.Repositories;

public interface IMedicalRecordPresence
{
    Task<bool> ExistsForPatientAsync(Guid patientId, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetPatientIdsWithRecordsAsync(
        IReadOnlyCollection<Guid> patientIds,
        CancellationToken cancellationToken);
}
