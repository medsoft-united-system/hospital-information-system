using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Contracts.Hl7Import;
using HospitalInformationSystem.Application.Contracts.ServiceAccounts;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public sealed class ServiceTokenValidator(ServiceTokenValidationParameters validation,
    IServiceTokenSessionService sessions) : IServiceTokenValidator, IHl7ImportTokenValidator
{
    private const int MaxTokenLength = 16 * 1024;

    public async Task<ServiceAccountIdentity?> ValidateAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > MaxTokenLength) return null;
        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false,
                MaximumTokenSizeInBytes = MaxTokenLength
            };
            principal = handler.ValidateToken(accessToken, validation.ValidationParameters, out _);
        }
        catch (SecurityTokenException) { return null; }
        catch (ArgumentException) { return null; }

        return await sessions.ValidatePrincipalAsync(principal, cancellationToken)
            ? ServiceAccountIdentityReader.TryRead(principal) : null;
    }

    public async Task<ServiceAccountIdentity?> ValidateImportAccessAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var identity = await ValidateAsync(accessToken, cancellationToken);
        return identity?.Role == ServiceAccountRole.Hl7Import ? identity : null;
    }
}
