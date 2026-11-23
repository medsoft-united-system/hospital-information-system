using System.Globalization;
using System.Security.Claims;
using HospitalInformationSystem.Models.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAuth;

namespace HospitalInformationSystem.Application.ServiceAccounts;

public static class ServiceAccountIdentityReader
{
    public static ServiceAccountIdentity? TryRead(ClaimsPrincipal? principal)
    {
        var identities = principal?.Identities.Where(identity => identity.IsAuthenticated &&
            identity.AuthenticationType == ServiceAuthenticationDefaults.Scheme).Take(2).ToArray();
        if (identities is not { Length: 1 }) return null;
        var identity = identities[0];
        var role = Single(identity, ServiceAuthenticationDefaults.Claims.Role) switch
        {
            ServiceAccountRoles.Lis => ServiceAccountRole.Lis,
            ServiceAccountRoles.Hl7Import => ServiceAccountRole.Hl7Import,
            _ => (ServiceAccountRole?)null
        };
        if (role is null ||
            Single(identity, ServiceAuthenticationDefaults.Claims.AccountKind) != ServiceAuthenticationDefaults.AccountKind ||
            !Guid.TryParseExact(Single(identity, ServiceAuthenticationDefaults.Claims.Subject), "D", out var id) ||
            id == Guid.Empty ||
            !long.TryParse(Single(identity, ServiceAuthenticationDefaults.Claims.AuthenticationVersion),
                NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            return null;
        var name = Single(identity, "name") ?? $"Сервисный аккаунт {Single(identity, ServiceAuthenticationDefaults.Claims.Role)}";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)) return null;
        return new ServiceAccountIdentity(id, name, version, role.Value);
    }

    private static string? Single(ClaimsIdentity identity, string type)
    {
        var claims = identity.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
