using System.Security.Claims;
using HospitalInformationSystem.Models.Auth;

namespace HospitalInformationSystem.Contracts.Abstractions;

public interface ITokenSessionService
{
    Task<TokenSessionResult?> IssueAsync(Guid userId, string expectedAuthenticationVersion,
        CancellationToken cancellationToken);
    Task<TokenSessionResult?> RotateAsync(string refreshToken, CancellationToken cancellationToken);
    Task<int> RevokeFamilyAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<int> RevokeFamilyAsync(string refreshToken, CancellationToken cancellationToken);
    Task<bool> ValidatePrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}
