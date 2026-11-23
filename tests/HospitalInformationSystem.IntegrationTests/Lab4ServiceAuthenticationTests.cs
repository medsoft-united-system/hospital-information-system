using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Hl7Import;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Models.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ServiceRole = HospitalInformationSystem.Models.ServiceAccounts.ServiceAccountRole;
using WorkerRole = HospitalInformationSystem.Models.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab4ServiceAuthenticationTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    [Theory]
    [InlineData(null)]
    [InlineData("LIS")]
    public async Task LisCreation_WithDefaultOrExplicitRole_ShouldPreserveExistingProbeJson(string? role)
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, role);
        account.Account.Role.Should().Be("LIS");
        var tokens = await factory.IssueAsync(account.ApiKey);
        using var client = factory.CreateMachineClient(tokens.AccessToken);
        using var response = await client.GetAsync("/api/testConnection");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ServiceAuthenticationApiTests.AssertTechnicalResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "status", "serviceAccountId", "role", "fhirVersion");
        (await response.Content.ReadFromJsonAsync<ServiceConnectionStatusDto>()).Should().BeEquivalentTo(
            new ServiceConnectionStatusDto("connected", account.Account.Id, "LIS", "5.0.0"));
        (await ValidateImportAsync(tokens.AccessToken)).Should().BeNull();
    }

    [Fact]
    public async Task Hl7ImportCreation_TokenAndRefresh_ShouldPreserveRoleAndAllowBothProbes()
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        account.Account.Role.Should().Be("HL7Import");
        var first = await factory.IssueAsync(account.ApiKey);
        AssertHl7Claims(first.AccessToken, account.Account.Id);
        await AssertHl7AccessAsync(first.AccessToken, account.Account.Id);
        factory.AdvanceTime(TimeSpan.FromSeconds(1));
        using var client = factory.CreateMachineClient();
        using var response = await client.PostAsJsonAsync("/api/service-auth/refresh",
            new ServiceRefreshRequest(first.RefreshToken));

        var next = await ServiceAuthenticationApiTests.PairAsync(response);

        next.AccessToken.Should().NotBe(first.AccessToken);
        next.RefreshToken.Should().NotBe(first.RefreshToken);
        next.RefreshTokenExpiresAt.Should().Be(first.RefreshTokenExpiresAt);
        AssertHl7Claims(next.AccessToken, account.Account.Id);
        await AssertHl7AccessAsync(first.AccessToken, account.Account.Id);
        await AssertHl7AccessAsync(next.AccessToken, account.Account.Id);
        using var read = await administrator.GetAsync($"/api/service-accounts/{account.Account.Id:D}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await read.Content.ReadFromJsonAsync<ServiceAccountDto>())!.Role.Should().Be("HL7Import");
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().ServiceAccounts
            .AsNoTracking().SingleAsync(item => item.Id == account.Account.Id);
        row.Role.Should().Be(ServiceRole.Hl7Import);
    }

    [Fact]
    public async Task Hl7ImportBearer_ShouldDenyEverySupportedFhirClinicalRouteAndHumanCrud()
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var tokens = await factory.IssueAsync(account.ApiKey);
        using var client = factory.CreateMachineClient(tokens.AccessToken);
        var id = Guid.NewGuid().ToString("D");
        var fhirRoutes = new[]
        {
            "/fhir/Patient/" + id, "/fhir/Practitioner/" + id,
            "/fhir/ServiceRequest/" + id, "/fhir/Observation/" + id,
            "/fhir/ServiceRequest", "/fhir/Observation"
        };
        foreach (var route in fhirRoutes)
        {
            using var response = await client.GetAsync(route);
            await AssertFhirForbiddenAsync(response, route);
        }
        using var observation = await client.PostAsync("/fhir/Observation",
            new StringContent("{}", Encoding.UTF8, "application/fhir+json"));
        await AssertFhirForbiddenAsync(observation, "/fhir/Observation POST");
        foreach (var route in new[] { "/api/auth/session", "/api/patients", "/api/accounts", "/api/service-accounts", "/api/audit" })
        {
            using var response = await client.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, route);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var route = method == HttpMethod.Post ? "/api/patients" : "/api/patients/" + id;
            using var request = new HttpRequestMessage(method, route) { Content = Json("{}") };
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, method + " " + route);
        }
        using var metadata = await client.GetAsync("/fhir/metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertHl7AccessAsync(tokens.AccessToken, account.Account.Id);
    }

    [Theory]
    [InlineData(WorkerRole.Doctor)]
    [InlineData(WorkerRole.Nurse)]
    [InlineData(WorkerRole.LaboratoryTechnician)]
    [InlineData(WorkerRole.Other)]
    public async Task MedicalWorkerBearer_ShouldProbeWithHumanIdentityAndLogoutShouldRevokeIt(WorkerRole role)
    {
        var credentials = await CreateHumanAsync(role);
        using var browser = factory.Application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        await ServiceAccountsApiTests.SetCsrfAsync(browser);
        using var login = await browser.PostAsJsonAsync("/api/auth/login", credentials);
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var tokens = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        using var bearer = factory.CreateMachineClient(tokens.AccessToken);

        await AssertHumanProbeAsync(bearer, tokens.Session.Id, ApplicationRoles.MedicalWorker);
        (await ValidateImportAsync(tokens.AccessToken)).Should().BeNull();
        using var fhir = await bearer.GetAsync("/fhir/ServiceRequest");
        fhir.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await ServiceAccountsApiTests.SetCsrfAsync(browser);
        using var logout = await browser.PostAsJsonAsync("/api/auth/logout", new { });
        logout.EnsureSuccessStatusCode();
        using var refused = await bearer.GetAsync("/api/testConnection");
        await ServiceAuthenticationApiTests.ErrorAsync(refused, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task AdministratorBearer_ShouldProbeWithTheActualHumanAccount()
    {
        using var administrator = await factory.AdministratorAsync();
        var session = (await administrator.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;

        await AssertHumanProbeAsync(administrator, session.Id, ApplicationRoles.Administrator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lis")]
    [InlineData("hl7import")]
    [InlineData("HL7Import ")]
    [InlineData("Administrator")]
    [InlineData("unknown")]
    public async Task CreateServiceAccount_UnknownRole_ShouldRejectWithoutCreatingAccount(string role)
    {
        using var administrator = await factory.AdministratorAsync();
        var name = "l2-invalid-" + Guid.NewGuid().ToString("N");
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts", new { name, role });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().ServiceAccounts.AnyAsync(row => row.Name == name))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("deactivate")]
    [InlineData("revoke")]
    public async Task Hl7ImportSession_AfterAccountSecurityChange_ShouldFailRawHttpAndRefresh(string operation)
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var first = await factory.IssueAsync(account.ApiKey);
        var second = await factory.IssueAsync(account.ApiKey);
        await AssertHl7AccessAsync(first.AccessToken, account.Account.Id);
        await AssertHl7AccessAsync(second.AccessToken, account.Account.Id);
        ServiceAccountKeyResultDto? rotated = null;
        if (operation == "rotate")
        {
            using var response = await administrator.PostAsJsonAsync($"/api/service-accounts/{account.Account.Id:D}/rotate-key",
                new RotateServiceAccountKeyRequest(account.Account.Version));
            response.EnsureSuccessStatusCode();
            rotated = (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
            rotated.RevokedSessionCount.Should().Be(2);
        }
        else if (operation == "deactivate")
        {
            using var response = await administrator.PutAsJsonAsync($"/api/service-accounts/{account.Account.Id:D}/active",
                new SetServiceAccountActiveRequest(false, account.Account.Version));
            response.EnsureSuccessStatusCode();
            (await response.Content.ReadFromJsonAsync<ServiceAccountActivityResultDto>())!.RevokedSessionCount.Should().Be(2);
        }
        else
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            (await scope.ServiceProvider.GetRequiredService<IServiceSessionRevoker>()
                .RevokeAllAsync(account.Account.Id, now, "l2-integration-revoke", CancellationToken.None)).Should().Be(2);
            await transaction.CommitAsync();
        }

        using var client = factory.CreateMachineClient();
        foreach (var pair in new[] { first, second })
        {
            await AssertAccessRejectedAsync(pair.AccessToken);
            using var refresh = await client.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(pair.RefreshToken));
            await ServiceAuthenticationApiTests.ErrorAsync(refresh, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        }
        if (rotated is not null)
        {
            rotated.Account.Role.Should().Be("HL7Import");
            var fresh = await factory.IssueAsync(rotated.ApiKey);
            await AssertHl7AccessAsync(fresh.AccessToken, account.Account.Id);
        }
    }

    [Fact]
    public async Task Hl7ImportRefreshReplay_ShouldRevokeRawAndHttpAccessForOnlyThatFamily()
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var first = await factory.IssueAsync(account.ApiKey);
        var independent = await factory.IssueAsync(account.ApiKey);
        using var client = factory.CreateMachineClient();
        using var refreshed = await client.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(first.RefreshToken));
        var next = await ServiceAuthenticationApiTests.PairAsync(refreshed);
        await AssertHl7AccessAsync(next.AccessToken, account.Account.Id);

        using var replay = await client.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(first.RefreshToken));

        await ServiceAuthenticationApiTests.ErrorAsync(replay, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        await AssertAccessRejectedAsync(first.AccessToken);
        await AssertAccessRejectedAsync(next.AccessToken);
        using var continuation = await client.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(next.RefreshToken));
        await ServiceAuthenticationApiTests.ErrorAsync(continuation, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        await AssertHl7AccessAsync(independent.AccessToken, account.Account.Id);
    }

    [Fact]
    public async Task Hl7ImportAccess_AtExactExpiry_ShouldFailRawAndHttpWhileRefreshRemainsUsable()
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        await AssertHl7AccessAsync(pair.AccessToken, account.Account.Id);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();

        factory.AdvanceTime(pair.AccessTokenExpiresAt - now);

        await AssertAccessRejectedAsync(pair.AccessToken);
        using var client = factory.CreateMachineClient();
        using var refreshed = await client.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(pair.RefreshToken));
        var next = await ServiceAuthenticationApiTests.PairAsync(refreshed);
        await AssertHl7AccessAsync(next.AccessToken, account.Account.Id);
    }

    [Fact]
    public async Task RawImportValidator_ShouldRejectInvalidSignatureIssuerAudienceRoleAndSessionClaims()
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var tokens = await factory.IssueAsync(account.ApiKey);
        await AssertHl7AccessAsync(tokens.AccessToken, account.Account.Id);
        var parts = tokens.AccessToken.Split('.');
        var signature = Base64UrlEncoder.DecodeBytes(parts[2]);
        signature[0] ^= 1;
        var corruptedSignature = parts[0] + "." + parts[1] + "." + Base64UrlEncoder.Encode(signature);
        var invalidTokens = new[]
        {
            "", "not-a-jwt", "Bearer " + tokens.AccessToken, corruptedSignature,
            SignModified(tokens.AccessToken, payload => payload[JwtRegisteredClaimNames.Iss] = "foreign-issuer"),
            SignModified(tokens.AccessToken, payload => payload[JwtRegisteredClaimNames.Aud] = "MedSoft.Web"),
            SignModified(tokens.AccessToken, payload => payload[JwtRegisteredClaimNames.Aud] = new[] { ServiceAuthenticationDefaults.Audience, "MedSoft.Web" }),
            SignModified(tokens.AccessToken, payload => payload["role"] = "LIS"),
            SignModified(tokens.AccessToken, payload => payload["role"] = "Administrator"),
            SignModified(tokens.AccessToken, payload => payload["role"] = new[] { "HL7Import", "LIS" }),
            SignModified(tokens.AccessToken, payload => payload["account_kind"] = "human"),
            SignModified(tokens.AccessToken, payload => payload["auth_ver"] = "2"),
            SignModified(tokens.AccessToken, payload => payload["sid"] = Guid.NewGuid().ToString("D")),
            SignModified(tokens.AccessToken, payload => payload["jti"] = Guid.NewGuid().ToString("D")),
            SignModified(tokens.AccessToken, payload => payload[JwtRegisteredClaimNames.Exp] = 1L),
            SignModified(tokens.AccessToken, payload => payload[JwtRegisteredClaimNames.Nbf] = DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        };
        foreach (var token in invalidTokens)
            await AssertAccessRejectedAsync(token);
        await AssertHl7AccessAsync(tokens.AccessToken, account.Account.Id);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("role")]
    [InlineData("version")]
    public async Task RawImportValidator_ShouldRecheckCurrentAccountEvenWhenSessionIsUnchanged(string change)
    {
        using var administrator = await factory.AdministratorAsync();
        var account = await CreateAsync(administrator, "HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        await AssertHl7AccessAsync(pair.AccessToken, account.Account.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var stored = await db.ServiceAccounts.SingleAsync(row => row.Id == account.Account.Id);
            if (change == "inactive") stored.IsActive = false;
            else if (change == "role") stored.Role = ServiceRole.Lis;
            else stored.AuthenticationVersion++;
            await db.SaveChangesAsync();
            var session = await db.ServiceRefreshSessions.AsNoTracking()
                .SingleAsync(row => row.ServiceAccountId == account.Account.Id);
            session.RevokedAtUtc.Should().BeNull();
        }

        await AssertAccessRejectedAsync(pair.AccessToken);
    }

    [Fact]
    public async Task RawImportValidator_SignedHl7ClaimOnLisSession_ShouldRejectPersistedRoleMismatch()
    {
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        var forgedRole = SignModified(pair.AccessToken, payload => payload["role"] = "HL7Import");

        await AssertAccessRejectedAsync(forgedRole);
        (await ValidateImportAsync(pair.AccessToken)).Should().BeNull();
        using var lis = factory.CreateMachineClient(pair.AccessToken);
        using var response = await lis.GetAsync("/api/testConnection");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<ServiceAccountKeyResultDto> CreateAsync(HttpClient administrator, string? role)
    {
        var name = "l2-auth-" + Guid.NewGuid().ToString("N");
        object request = role is null ? new { name } : new { name, role };
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
    }

    private async Task<LoginRequest> CreateHumanAsync(WorkerRole role)
    {
        var userName = "l2." + Guid.NewGuid().ToString("N")[..12];
        const string password = "Lab4.Service-Matrix.6249!";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var worker = new MedicalWorkerDbo
        {
            Id = Guid.NewGuid(), FirstName = "Проверка", LastName = "Подключения", Position = "Сотрудник",
            Role = role, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.MedicalWorkers.Add(worker);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
            new CreateAccountRequest(userName, password, ApplicationRoles.MedicalWorker, worker.Id), CancellationToken.None);
        return new LoginRequest(userName, password);
    }

    private async Task<ServiceAccountIdentity?> ValidateImportAsync(string token)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHl7ImportTokenValidator>()
            .ValidateImportAccessAsync(token, CancellationToken.None);
    }

    private async Task AssertHl7AccessAsync(string token, Guid accountId)
    {
        var identity = await ValidateImportAsync(token);
        identity.Should().NotBeNull();
        identity!.Id.Should().Be(accountId);
        identity.Role.Should().Be(ServiceRole.Hl7Import);
        using var client = factory.CreateMachineClient(token);
        using var response = await client.GetAsync("/api/testConnection");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        ServiceAuthenticationApiTests.AssertTechnicalResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "status", "serviceAccountId", "role", "hl7Version");
        json.RootElement.GetProperty("status").GetString().Should().Be("connected");
        json.RootElement.GetProperty("serviceAccountId").GetGuid().Should().Be(accountId);
        json.RootElement.GetProperty("role").GetString().Should().Be("HL7Import");
        json.RootElement.GetProperty("hl7Version").GetString().Should().Be("2.8");
    }

    private async Task AssertAccessRejectedAsync(string token)
    {
        (await ValidateImportAsync(token)).Should().BeNull();
        using var client = factory.CreateMachineClient(token);
        using var response = await client.GetAsync("/api/testConnection");
        await ServiceAuthenticationApiTests.ErrorAsync(response, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
    }

    private static async Task AssertHumanProbeAsync(HttpClient client, Guid accountId, string role)
    {
        using var response = await client.GetAsync("/api/testConnection");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        ServiceAuthenticationApiTests.AssertTechnicalResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "status", "accountId", "role", "accountKind");
        json.RootElement.GetProperty("status").GetString().Should().Be("connected");
        json.RootElement.GetProperty("accountId").GetGuid().Should().Be(accountId);
        json.RootElement.GetProperty("role").GetString().Should().Be(role);
        json.RootElement.GetProperty("accountKind").GetString().Should().Be("human");
    }

    private static async Task AssertFhirForbiddenAsync(HttpResponseMessage response, string route)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, route + " " + await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("resourceType").GetString().Should().Be("OperationOutcome");
    }

    private string SignModified(string token, Action<JwtPayload> change)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var original = handler.ReadJwtToken(token);
        change(original.Payload);
        var keys = factory.Services.GetRequiredService<JwtKeyMaterial>();
        return handler.WriteToken(new JwtSecurityToken(new JwtHeader(keys.SigningCredentials), original.Payload));
    }

    private static void AssertHl7Claims(string token, Guid accountId)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        jwt.Issuer.Should().Be(ServiceAuthenticationDefaults.Issuer);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(ServiceAuthenticationDefaults.Audience);
        jwt.Claims.Single(claim => claim.Type == "sub").Value.Should().Be(accountId.ToString("D"));
        jwt.Claims.Single(claim => claim.Type == "role").Value.Should().Be("HL7Import");
        jwt.Claims.Single(claim => claim.Type == "account_kind").Value.Should().Be("service");
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
