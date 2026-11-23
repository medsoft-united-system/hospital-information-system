using HospitalInformationSystem.Models.Patients;

namespace HospitalInformationSystem.Abstractions.Repositories;

public interface IPatientRepository
{
    Task<IReadOnlyList<Patient>> ListAsync(CancellationToken cancellationToken);
    Task<Patient?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    void Add(Patient entity);
    Task UpdateAsync(Patient entity, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

