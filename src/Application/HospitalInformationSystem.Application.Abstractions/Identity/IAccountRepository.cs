using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.MedicalWorkers;

namespace HospitalInformationSystem.Application.Abstractions.Identity;

public interface IAccountRepository
{
    Task<IReadOnlyList<AccountIdentity>> ListAsync(CancellationToken cancellationToken);
    Task<AccountIdentity?> FindAsync(Guid accountId, CancellationToken cancellationToken);
    Task<AccountIdentity?> FindForUpdateAsync(Guid accountId, CancellationToken cancellationToken);
    Task<MedicalWorker?> FindMedicalWorkerForUpdateAsync(Guid medicalWorkerId, CancellationToken cancellationToken);
    Task<bool> HasMedicalWorkerAccountAsync(Guid medicalWorkerId, CancellationToken cancellationToken);
    Task<bool> HasAdministratorAsync(CancellationToken cancellationToken);
    Task CreateAsync(AccountIdentity account, string password, CancellationToken cancellationToken);
    Task ResetPasswordAsync(Guid accountId, string password, CancellationToken cancellationToken);
    Task SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken);
    Task<int> RevokeRefreshSessionsAsync(Guid accountId, string reason, DateTimeOffset now,
        CancellationToken cancellationToken);
}
