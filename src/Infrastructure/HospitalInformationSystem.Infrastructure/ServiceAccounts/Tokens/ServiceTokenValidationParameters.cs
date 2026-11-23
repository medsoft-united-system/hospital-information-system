using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public sealed class ServiceTokenValidationParameters(JwtKeyMaterial keys, IOptions<ServiceTokenOptions> options,
    TimeProvider timeProvider)
{
    public TokenValidationParameters ValidationParameters => new()
    {
        RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = keys.ValidationKey,
        TryAllIssuerSigningKeys = false, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidateIssuer = true, ValidIssuer = options.Value.Issuer,
        ValidateAudience = true, ValidAudience = options.Value.Audience,
        AudienceValidator = (audiences, _, _) => audiences is not null &&
            audiences.Take(2).SequenceEqual([options.Value.Audience], StringComparer.Ordinal),
        RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        AuthenticationType = ServiceAuthenticationDefaults.Scheme,
        NameClaimType = "name", RoleClaimType = ServiceAuthenticationDefaults.Claims.Role,
        LifetimeValidator = (notBefore, expires, _, _) => notBefore is not null && expires is not null &&
            notBefore.Value < expires.Value && notBefore.Value <= timeProvider.GetUtcNow().UtcDateTime &&
            expires.Value > timeProvider.GetUtcNow().UtcDateTime
    };
}
