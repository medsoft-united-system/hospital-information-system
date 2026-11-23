using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.ServiceAccounts;
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
    [InlineData(ServiceAccountRoles.Lis, HttpStatusCode.Forbidden)]
    [InlineData(ServiceAccountRoles.Hl7Import, HttpStatusCode.Forbidden)]
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

    [Theory]
    [InlineData(ServiceAccountRoles.Lis)]
    [InlineData(ServiceAccountRoles.Hl7Import)]
    public async Task UiMock_AdminLifecycle_PreservesIdentityAndKeepsKeysOutOfLists(string role)
    {
        var session = new SessionState();
        var state = new UiMockState(session);
        state.SwitchPersona(UiMockPersona.Administrator);
        using var http = new HttpClient(new UiMockHttpMessageHandler(state)) { BaseAddress = new Uri("https://ui-mock.invalid/") };
        var gateway = new ServiceAccountsGateway(new ServiceAccountsApiClient(http), session);
        var created = await gateway.CreateAsync(new CreateServiceAccountRequest("Новая лаборатория", role), CancellationToken.None);
        var renamed = await gateway.UpdateAsync(created.Account.Id, new UpdateServiceAccountRequest("Основная лаборатория", created.Account.Version), CancellationToken.None);
        var disabled = await gateway.SetActiveAsync(renamed.Id, new SetServiceAccountActiveRequest(false, renamed.Version), CancellationToken.None);
        var rotated = await gateway.RotateKeyAsync(disabled.Account.Id, new RotateServiceAccountKeyRequest(disabled.Account.Version), CancellationToken.None);
        (await gateway.GetAsync(rotated.Account.Id, CancellationToken.None)).Should().Be(rotated.Account);
        rotated.Account.Id.Should().Be(created.Account.Id);
        created.Account.Role.Should().Be(role);
        renamed.Role.Should().Be(role);
        disabled.Account.Role.Should().Be(role);
        rotated.Account.Role.Should().Be(role);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hl7Import")]
    [InlineData("Administrator")]
    public async Task UiMock_CreateRejectsUnsupportedRoleWithoutCreatingAccount(string? role)
    {
        var session = new SessionState();
        var state = new UiMockState(session);
        state.SwitchPersona(UiMockPersona.Administrator);
        using var http = new HttpClient(new UiMockHttpMessageHandler(state)) { BaseAddress = new Uri("https://ui-mock.invalid/") };
        var api = new ServiceAccountsApiClient(http);
        var before = await api.ListAsync(CancellationToken.None);

        using var response = await http.PostAsJsonAsync("api/service-accounts", new { name = "Импорт пациентов", role });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.ListAsync(CancellationToken.None)).Should().Equal(before);
    }

    [Fact]
    public async Task UiMock_CreateWithOmittedRoleUsesLisDefault()
    {
        var session = new SessionState();
        var state = new UiMockState(session);
        state.SwitchPersona(UiMockPersona.Administrator);
        using var http = new HttpClient(new UiMockHttpMessageHandler(state)) { BaseAddress = new Uri("https://ui-mock.invalid/") };

        using var response = await http.PostAsJsonAsync("api/service-accounts", new { name = "Совместимая лаборатория" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var saved = await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>();
        saved!.Account.Role.Should().Be(ServiceAccountRoles.Lis);
    }

    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called.");
    }
}
