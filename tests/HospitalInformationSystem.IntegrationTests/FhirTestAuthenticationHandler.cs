using System.Security.Claims;
using System.Text.Encodings.Web;
using HospitalInformationSystem.Contracts.ServiceAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.IntegrationTests;

internal sealed class FhirTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString();
        if (token is not ("FhirTest lis" or "FhirTest denied"))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[]
        {
            new Claim("sub", FhirReadApiFactory.ServiceId.ToString("D")),
            new Claim("account_kind", "service"),
            new Claim("role", token == "FhirTest lis" ? "LIS" : "Other")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ServiceAuthenticationDefaults.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,
            ServiceAuthenticationDefaults.Scheme)));
    }
}
