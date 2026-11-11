using HospitalInformationSystem.Contracts.ServiceAuth;
using Microsoft.AspNetCore.Authentication;

namespace HospitalInformationSystem.Api.Fhir;

public static class FhirRegistration
{
    public static IServiceCollection AddFhirReadApi(this IServiceCollection services)
    {
        services.AddSingleton<FhirEndpointOptions>();
        services.AddSingleton<FhirCursorProtector>();
        services.AddSingleton<FhirSearchParser>();
        services.AddScoped<FhirResponseWriter>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, UnavailableServiceAuthenticationHandler>(
            ServiceAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(ServiceAuthenticationDefaults.Policies.FhirRead,
            policy => policy.AddAuthenticationSchemes(ServiceAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(ServiceAuthenticationDefaults.Claims.AccountKind, ServiceAuthenticationDefaults.AccountKind)
                .RequireClaim(ServiceAuthenticationDefaults.Claims.Role, ServiceAuthenticationDefaults.Role)));
        return services;
    }
}
