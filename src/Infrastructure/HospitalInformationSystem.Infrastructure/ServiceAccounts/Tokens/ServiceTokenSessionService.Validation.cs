using System.Globalization;
using System.Security.Claims;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Domain.ServiceAccounts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public sealed partial class ServiceTokenSessionService
{
    public async Task<bool> ValidatePrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities is not { Length: 1 } ||
            identities[0].AuthenticationType != ServiceAuthenticationDefaults.Scheme)
            return false;
        var service = ServiceAccountIdentityReader.TryRead(principal);
        if (service is null) return false;
        var identity = identities[0];
        if (Single(identity, ServiceAuthenticationDefaults.Claims.AccountKind) != ServiceAuthenticationDefaults.AccountKind ||
            !Identifier(identity, ServiceAuthenticationDefaults.Claims.Subject, out var id) ||
            !Identifier(identity, ServiceAuthenticationDefaults.Claims.SessionId, out var familyId) ||
            !Identifier(identity, ServiceAuthenticationDefaults.Claims.TokenId, out var sessionId) ||
            !Number(identity, ServiceAuthenticationDefaults.Claims.AuthenticationVersion, out var version) || version < 1 ||
            !Number(identity, ServiceAuthenticationDefaults.Claims.IssuedAt, out var issued) ||
            !Number(identity, ServiceAuthenticationDefaults.Claims.NotBefore, out var notBefore) || issued != notBefore ||
            !Number(identity, ServiceAuthenticationDefaults.Claims.ExpiresAt, out var expires) || expires <= issued)
            return false;
        var now = timeProvider.GetUtcNow();
        if (issued > now.ToUnixTimeSeconds() || expires <= now.ToUnixTimeSeconds() ||
            expires - issued > options.Value.AccessTokenMinutes * 60L)
            return false;
        DateTimeOffset issuedAt;
        DateTimeOffset expiresAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(issued);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
        }
        catch (ArgumentOutOfRangeException) { return false; }
        var nextIssuedSecond = issuedAt.AddSeconds(1);
        return await db.ServiceRefreshSessions.AsNoTracking().AnyAsync(session =>
                session.Id == sessionId && session.ServiceAccountId == id && session.FamilyId == familyId &&
                session.AuthenticationVersion == version && session.RevokedAtUtc == null &&
                session.CreatedAtUtc <= now && session.CreatedAtUtc >= issuedAt && session.CreatedAtUtc < nextIssuedSecond &&
                session.ExpiresAtUtc > now && session.ExpiresAtUtc >= expiresAt &&
                db.ServiceAccounts.Any(account => account.Id == id && account.IsActive &&
                    account.Role == service.Role && account.AuthenticationVersion == version) &&
                !db.ServiceRefreshSessions.Any(row => row.ServiceAccountId == id && row.FamilyId == familyId &&
                                                     row.RevokedAtUtc != null), cancellationToken);
    }

    private static bool Identifier(ClaimsIdentity identity, string type, out Guid id) =>
        Guid.TryParseExact(Single(identity, type), "D", out id) && id != Guid.Empty;

    private static bool Number(ClaimsIdentity identity, string type, out long value) =>
        long.TryParse(Single(identity, type), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string? Single(ClaimsIdentity identity, string type)
    {
        var claims = identity.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
