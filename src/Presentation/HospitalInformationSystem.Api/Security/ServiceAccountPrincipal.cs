using System.Globalization;
using System.Security.Claims;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Contracts.ServiceAuth;

namespace HospitalInformationSystem.Api.Security;

public static class ServiceAccountPrincipal
{
    public static ServiceAccountIdentity? TryGetIdentity(ClaimsPrincipal? principal)
    {
        var identities = principal?.Identities.Where(identity => identity.IsAuthenticated &&
            identity.AuthenticationType == ServiceAuthenticationDefaults.Scheme).ToArray();
        if (identities is not { Length: 1 }) return null;
        var identity = identities[0];
        if (Single(identity, ServiceAuthenticationDefaults.Claims.AccountKind) != ServiceAuthenticationDefaults.AccountKind ||
            Single(identity, ServiceAuthenticationDefaults.Claims.Role) != ServiceAuthenticationDefaults.Role ||
            !Guid.TryParseExact(Single(identity, ServiceAuthenticationDefaults.Claims.Subject), "D", out var id) ||
            id == Guid.Empty ||
            !long.TryParse(Single(identity, ServiceAuthenticationDefaults.Claims.AuthenticationVersion),
                NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            return null;
        var name = Single(identity, "name") ?? "Сервисный аккаунт LIS";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)) return null;
        return new ServiceAccountIdentity(id, name, version);
    }

    private static string? Single(ClaimsIdentity identity, string type)
    {
        var claims = identity.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
