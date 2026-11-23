using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Contracts.Abstractions;

public interface IAdministratorSetupService
{
    Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<SetupChallengeDto> BeginAsync(BeginSetupRequest request, string ownerToken, CancellationToken cancellationToken);
    Task ConfirmAsync(ConfirmSetupRequest request, string ownerToken, CancellationToken cancellationToken);
    Task<bool> VerifyAdministratorCodeAsync(Guid userId, string? code, CancellationToken cancellationToken);
}
