using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Contracts.Abstractions;

public interface IAccountService
{
    Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken cancellationToken);
    Task<AccountDto> CreateAsync(CreateAccountRequest request, CancellationToken cancellationToken);
    Task<SessionDto?> GetSessionAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountDetailsDto>> ListDetailsAsync(CancellationToken cancellationToken);
    Task<CreateWorkerAccountResultDto> CreateForWorkerAsync(Guid medicalWorkerId, CreateWorkerAccountRequest request,
        CancellationToken cancellationToken);
    Task<ResetAccountPasswordResultDto> ResetPasswordAsync(Guid accountId, CancellationToken cancellationToken);
    Task<SetAccountActiveResultDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken);
}
