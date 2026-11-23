using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Persistence;

namespace HospitalInformationSystem.Abstractions.Identity;

public interface IAdministratorSetupStore
{
    Task<bool> IsCompletedAsync(CancellationToken cancellationToken);
    Task LockSetupAsync(CancellationToken cancellationToken);
    Task<AdministratorSetupAccount?> FindAdministratorAsync(CancellationToken cancellationToken);
    Task<AdministratorSetupAccount?> FindAccountAsync(Guid userId, CancellationToken cancellationToken);
    Task<string> HashPasswordAsync(AdministratorSetupAccount account, string password,
        CancellationToken cancellationToken);
    Task ReplaceChallengesAsync(AdministratorSetupChallengeDbo challenge, CancellationToken cancellationToken);
    Task<AdministratorSetupChallengeDbo?> FindChallengeAsync(Guid challengeId, CancellationToken cancellationToken);
    void RemoveChallenge(AdministratorSetupChallengeDbo challenge);
    Task SaveAccountAsync(AdministratorSetupAccount account, string passwordHash, bool created, string role,
        CancellationToken cancellationToken);
    Task<AdministratorSetupStateDbo?> FindStateAsync(CancellationToken cancellationToken);
    Task<AdministratorSetupStateDbo?> FindCompletedStateAsync(Guid userId, CancellationToken cancellationToken);
    void AddState(AdministratorSetupStateDbo state);
    void AddVerification(AdministratorTotpVerificationDbo verification);
    Task<AdministratorTotpVerificationDbo?> FindVerificationForUpdateAsync(Guid userId,
        CancellationToken cancellationToken);
    Task<bool> IsAdministratorAvailableAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}
