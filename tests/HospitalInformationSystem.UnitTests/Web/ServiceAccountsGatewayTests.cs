using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Development;
using HospitalInformationSystem.Web.Gateways;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class ServiceAccountsGatewayTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(ApplicationRoles.MedicalWorker, HttpStatusCode.Forbidden)]
    [InlineData("LIS", HttpStatusCode.Forbidden)]
    public async Task EveryOperation_RejectsNonAdministratorBeforeHttp(string? role, HttpStatusCode status)
    {
        var session = new SessionState();
        if (role is not null) session.SetSession(new SessionDto(Guid.NewGuid(), "user", role, null));
        using var http = new HttpClient(new RejectHandler()) { BaseAddress = new Uri("https://localhost/") };
        var gateway = new ServiceAccountsGateway(new ServiceAccountsApiClient(http), session);
        Func<Task>[] operations =
        [
            () => gateway.ListAsync(CancellationToken.None),
            () => gateway.GetAsync(Guid.NewGuid(), CancellationToken.None),
            () => gateway.CreateAsync(new CreateServiceAccountRequest("lab"), CancellationToken.None),
            () => gateway.UpdateAsync(Guid.NewGuid(), new UpdateServiceAccountRequest("lab", 1), CancellationToken.None),
            () => gateway.SetActiveAsync(Guid.NewGuid(), new SetServiceAccountActiveRequest(false, 1), CancellationToken.None),
            () => gateway.RotateKeyAsync(Guid.NewGuid(), new RotateServiceAccountKeyRequest(1), CancellationToken.None)
        ];
        foreach (var operation in operations)
            (await operation.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task UiMock_AdminLifecycle_PreservesIdentityAndKeepsKeysOutOfLists()
    {
        var session = new SessionState();
        var state = new UiMockState(session);
        state.SwitchPersona(UiMockPersona.Administrator);
        using var http = new HttpClient(new UiMockHttpMessageHandler(state)) { BaseAddress = new Uri("https://ui-mock.invalid/") };
        var gateway = new ServiceAccountsGateway(new ServiceAccountsApiClient(http), session);
        var created = await gateway.CreateAsync(new CreateServiceAccountRequest("Новая лаборатория"), CancellationToken.None);
        var renamed = await gateway.UpdateAsync(created.Account.Id, new UpdateServiceAccountRequest("Основная лаборатория", created.Account.Version), CancellationToken.None);
        var disabled = await gateway.SetActiveAsync(renamed.Id, new SetServiceAccountActiveRequest(false, renamed.Version), CancellationToken.None);
        var rotated = await gateway.RotateKeyAsync(disabled.Account.Id, new RotateServiceAccountKeyRequest(disabled.Account.Version), CancellationToken.None);
        (await gateway.GetAsync(rotated.Account.Id, CancellationToken.None)).Should().Be(rotated.Account);
        rotated.Account.Id.Should().Be(created.Account.Id);
        rotated.Account.IsActive.Should().BeFalse();
        rotated.Account.Name.Should().Be("Основная лаборатория");
        rotated.ApiKey.Should().NotBe(created.ApiKey);
        var list = await gateway.ListAsync(CancellationToken.None);
        list.Should().Contain(rotated.Account);
        var json = await http.GetStringAsync("api/service-accounts");
        json.Should().NotContain("apiKey").And.NotContain(created.ApiKey).And.NotContain(rotated.ApiKey);
        using var stale = await http.PutAsJsonAsync($"api/service-accounts/{created.Account.Id:D}/active",
            new SetServiceAccountActiveRequest(true, created.Account.Version));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        state.SwitchPersona(UiMockPersona.Doctor);
        using var medical = await http.GetAsync("api/service-accounts");
        medical.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        state.Reset();
        state.SwitchPersona(UiMockPersona.Administrator);
        (await gateway.ListAsync(CancellationToken.None)).Should().NotContain(x => x.Id == created.Account.Id);
    }

    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called.");
    }
}
