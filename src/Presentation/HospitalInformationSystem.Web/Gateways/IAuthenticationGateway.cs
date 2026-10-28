using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Web.Gateways;

public interface IAuthenticationGateway
{
    Task<SetupStatusDto> GetSetupStatusAsync(CancellationToken cancellationToken);
    Task<SetupChallengeDto> BeginSetupAsync(BeginSetupRequest request, string setupToken,
        CancellationToken cancellationToken);
    Task ConfirmSetupAsync(ConfirmSetupRequest request, string setupToken,
        CancellationToken cancellationToken);
    Task<AuthenticationSessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AdministratorLoginChallengeDto> BeginAdministratorLoginAsync(BeginAdministratorLoginRequest request,
        CancellationToken cancellationToken);
    Task<AdministratorLoginChallengeDto> GetAdministratorLoginChallengeAsync(CancellationToken cancellationToken);
    Task<AuthenticationSessionDto> ConfirmAdministratorLoginAsync(ConfirmAdministratorLoginRequest request,
        CancellationToken cancellationToken);
    Task<AuthenticationSessionDto?> GetSessionAsync(CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
}
