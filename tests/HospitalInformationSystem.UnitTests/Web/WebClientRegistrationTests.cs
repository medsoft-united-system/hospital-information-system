using System.Threading;
using System.Net;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Configuration;
using HospitalInformationSystem.Web.Development;
using HospitalInformationSystem.Web.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class WebClientRegistrationTests
{
    [Fact]
    public async Task AddWebClient_MockConfiguration_ShouldWorkWithoutApiAddress()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["UiMock:Enabled"] = "true" }).Build();
        var services = new ServiceCollection().AddHospitalWebClients(configuration, "UiMock");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider;

        using var anonymous = await resolved.GetRequiredService<HttpClient>().GetAsync("api/auth/session");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resolved.GetRequiredService<HttpClient>().BaseAddress!.Host.Should().Be("ui-mock.invalid");
        resolved.GetRequiredService<UiMockState>().SwitchPersona(UiMockPersona.Doctor);
        var session = await resolved.GetRequiredService<IAuthenticationGateway>().GetSessionAsync(CancellationToken.None);
        session!.MedicalWorkerRole.Should().Be(MedicalWorkerRole.Doctor);
        var workers = await resolved.GetRequiredService<MedicalWorkersApiClient>()
            .ListAsync(false, CancellationToken.None);
        workers.Should().NotBeEmpty();
        resolved.GetRequiredService<IAccountManagementGateway>().Should().NotBeNull();
        resolved.GetRequiredService<IAuditGateway>().Should().NotBeNull();
        resolved.GetRequiredService<IMedicalRecordsGateway>().Should().NotBeNull();
    }

    [Fact]
    public void AddWebClient_NormalConfiguration_ShouldOmitMockStateAndTransport()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.example.invalid/" }).Build();
        var services = new ServiceCollection().AddHospitalWebClients(configuration, "Production");
        using var provider = services.BuildServiceProvider();

        provider.GetService<UiMockState>().Should().BeNull();
        provider.GetService<UiMockHttpMessageHandler>().Should().BeNull();
        provider.GetRequiredService<HttpClient>().BaseAddress.Should().Be(new Uri("https://api.example.invalid/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api")]
    [InlineData("http://localhost:5200/")]
    [InlineData("ftp://api.example.invalid/")]
    [InlineData("https://user:password@api.example.invalid/")]
    [InlineData("https://api.example.invalid/api/")]
    [InlineData("https://api.example.invalid/?token=unexpected")]
    [InlineData("https://api.example.invalid/#fragment")]
    public void AddWebClient_InvalidApiAddress_ShouldRejectBeforeCreatingTransport(string? address)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ApiBaseUrl"] = address }).Build();

        foreach (var environment in new[] { "Development", "Production" })
        {
            var action = () => new ServiceCollection().AddHospitalWebClients(configuration, environment);

            action.Should().Throw<InvalidOperationException>().WithMessage("*ApiBaseUrl*HTTPS*");
        }
    }

    [Theory]
    [InlineData("https://localhost:5201/")]
    [InlineData("https://127.0.0.1:5201/")]
    [InlineData("https://[::1]:5201/")]
    public void AddWebClient_ExplicitHttpsOrigin_ShouldPreserveApiAddress(string address)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ApiBaseUrl"] = address }).Build();
        var services = new ServiceCollection().AddHospitalWebClients(configuration, "Development");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<HttpClient>().BaseAddress.Should().Be(new Uri(address));
    }
}
