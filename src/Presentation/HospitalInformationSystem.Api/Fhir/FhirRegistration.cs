using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.ServiceAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HospitalInformationSystem.Api.Fhir;

public static class FhirRegistration
{
    public static IServiceCollection AddFhirApi(this IServiceCollection services)
    {
        services.AddSingleton<FhirEndpointOptions>();
        services.AddSingleton<FhirCursorProtector>();
        services.AddSingleton<FhirSearchParser>();
        services.AddScoped<FhirResponseWriter>();
        services.AddScoped<FhirObservationReader>();
        services.Replace(ServiceDescriptor.Scoped<ICurrentServiceAccount, CurrentServiceAccount>());
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, UnavailableServiceAuthenticationHandler>(
            ServiceAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            foreach (var name in new[] { ServiceAuthenticationDefaults.Policies.FhirRead,
                         ServiceAuthenticationDefaults.Policies.ObservationCreate })
                options.AddPolicy(name, policy => policy.AddAuthenticationSchemes(ServiceAuthenticationDefaults.Scheme)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => ServiceAccountPrincipal.TryGetIdentity(context.User) is not null));
        });
        return services;
    }
}
