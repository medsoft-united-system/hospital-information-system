using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Contracts.Abstractions;
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
        return services;
    }
}
