using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public static class Hl7TransportRegistration
{
    public static IServiceCollection AddHl7MllpTransport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<Hl7ImportOptions>, Hl7ImportOptionsValidator>();
        services.AddOptions<Hl7ImportOptions>().Bind(configuration.GetSection(Hl7ImportOptions.SectionName))
            .ValidateOnStart();
        services.AddHostedService<Hl7MllpListener>();
        return services;
    }
}
