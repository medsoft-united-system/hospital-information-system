using System.Net.Http.Headers;
using System.Net.Http.Json;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuthenticationApiFactory : IAsyncLifetime
{
    private readonly SecureHospitalApiFactory _security = new();
    private WebApplicationFactory<Program>? _application;

    public WebApplicationFactory<Program> Application => _application
        ?? throw new InvalidOperationException("The service authentication test host is not initialized.");

    public IServiceProvider Services => Application.Services;

    public HttpClient CreateMachineClient(string? accessToken = null)
    {
        var client = Application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task<HttpClient> AdministratorAsync()
    {
        var client = Application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        await ServiceAccountsApiTests.SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", _security.AdministratorLogin())).EnsureSuccessStatusCode();
        await ServiceAccountsApiTests.SetCsrfAsync(client);
        return client;
    }

    public async Task<ServiceAccountKeyResultDto> CreateAccountAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceAccountService>().CreateAsync(
            new CreateServiceAccountRequest("lis-auth-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
    }

    public async Task<ServiceTokenPairDto> IssueAsync(string apiKey)
    {
        using var client = CreateMachineClient();
        using var response = await client.PostAsJsonAsync("/api/service-auth/token", new ServiceTokenRequest(apiKey));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceTokenPairDto>())!;
    }

    public void AdvanceTime(TimeSpan duration) => _security.AdvanceTime(duration);

    public async Task InitializeAsync()
    {
        await _security.InitializeAsync();
        _application = _security.WithWebHostBuilder(builder =>
            builder.UseSetting("Security:ServiceAuthRateLimit:PermitLimit", "1000"));
        _ = Application.Services;
    }

    public async Task DisposeAsync()
    {
        if (_application is not null) await _application.DisposeAsync();
        await ((IAsyncLifetime)_security).DisposeAsync();
    }
}
