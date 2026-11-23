using HospitalInformationSystem.Application.Models.MedicalWorkers;

namespace HospitalInformationSystem.Application.Abstractions.Repositories;

public interface IMedicalWorkerRepository
{
    Task<IReadOnlyList<MedicalWorker>> ListAsync(bool includeDeleted, CancellationToken cancellationToken);
    Task<MedicalWorker?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    void Add(MedicalWorker worker);
    Task UpdateAsync(MedicalWorker worker, CancellationToken cancellationToken);
}
