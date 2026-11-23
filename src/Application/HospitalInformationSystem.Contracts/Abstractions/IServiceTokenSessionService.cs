using System.Security.Claims;
using HospitalInformationSystem.Models.ServiceAuth;

namespace HospitalInformationSystem.Contracts.Abstractions;

public interface IServiceTokenSessionService
{
    Task<ServiceTokenPairDto?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken);
    Task<ServiceTokenPairDto?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken);
    Task<bool> ValidatePrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}
