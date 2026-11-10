using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Development;
using HospitalInformationSystem.Web.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Web.Configuration;

public static class WebClientRegistration
{
    public static IServiceCollection AddHospitalWebClients(this IServiceCollection services,
        IConfiguration configuration, string environment)
    {
        var mock = UiMockOptions.FromConfiguration(configuration, environment);
        var apiOrigin = mock.Enabled ? new Uri("https://ui-mock.invalid/")
            : RequireApiOrigin(configuration["ApiBaseUrl"]);
        services.AddSingleton(mock);
        services.AddScoped<SessionState>();
        services.AddScoped<AntiforgeryTokenStore>();
        services.AddScoped<AuthenticationOperationCoordinator>();
        if (!mock.Enabled)
            services.AddScoped<IBrowserTabSession, BrowserTabSessionAccessor>();
        if (mock.Enabled)
        {
            services.AddScoped<UiMockState>();
            services.AddScoped<UiMockHttpMessageHandler>();
        }

        services.AddScoped(provider => new HttpClient(new BrowserSessionHandler(
            provider.GetRequiredService<SessionState>(), provider.GetRequiredService<AntiforgeryTokenStore>(),
            provider.GetRequiredService<AuthenticationOperationCoordinator>(), apiOrigin,
            mock.Enabled ? null : provider.GetRequiredService<IBrowserTabSession>())
        {
            InnerHandler = mock.Enabled ? provider.GetRequiredService<UiMockHttpMessageHandler>() : new HttpClientHandler()
        })
        {
            BaseAddress = apiOrigin,
            Timeout = TimeSpan.FromSeconds(20)
        });

        services.AddScoped<MedicalWorkersApiClient>();
        services.AddScoped<DiagnosesApiClient>();
        services.AddScoped<LaboratoryTestsApiClient>();
        services.AddScoped<PatientsApiClient>();
        services.AddScoped<MedicalRecordsApiClient>();
        services.AddScoped<SessionApiClient>();
        services.AddScoped<AccountsApiClient>();
        services.AddScoped<ServiceAccountsApiClient>();
        services.AddScoped<IAuthenticationGateway, AuthenticationGateway>();
        services.AddScoped<IAccountManagementGateway, AccountManagementGateway>();
        services.AddScoped<IAuditGateway, AuditGateway>();
        services.AddScoped<IMedicalRecordsGateway, MedicalRecordsGateway>();
        services.AddScoped<ILaboratoryTestsGateway, LaboratoryTestsGateway>();
        services.AddScoped<IServiceAccountsGateway, ServiceAccountsGateway>();
        return services;
    }

    private static Uri RequireApiOrigin(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Host.Contains('*'))
            throw new InvalidOperationException(
                "ApiBaseUrl должен содержать абсолютный HTTPS-адрес API без пути, параметров и учётных данных.");
        return uri;
    }
}
