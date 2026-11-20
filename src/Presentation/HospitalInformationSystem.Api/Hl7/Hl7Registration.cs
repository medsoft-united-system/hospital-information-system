namespace HospitalInformationSystem.Api.Hl7;

public static class Hl7Registration
{
    public static IServiceCollection AddHl7ImportCodec(this IServiceCollection services)
    {
        services.AddSingleton<IHl7MessageCodec, Hl7MessageCodec>();
        services.AddScoped<Hl7MessageProcessor>();
        return services;
    }
}
