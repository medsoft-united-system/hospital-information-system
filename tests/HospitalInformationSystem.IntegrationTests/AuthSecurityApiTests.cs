using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuthSecurityApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    private const string CsrfHeader = "X-CSRF-TOKEN";

    [Theory]
    [InlineData("/api/medical-workers")]
    [InlineData("/api/diagnoses")]
    [InlineData("/api/patients")]
    [InlineData("/api/accounts")]
    [InlineData("/api/auth/session")]
    public async Task Send_AnonymousRequest_ShouldDenyHospitalAndAccountData(string route)
    {
        using var client = factory.CreateCookieClient();

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Login_ValidCredentials_ShouldIssueRs256AccessAndHttpOnlyRefresh()
    {
        using var client = factory.CreateCookieClient();
        await RefreshCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/login",
            factory.AdministratorLogin());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
            cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        var session = (await client.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        session.UserName.Should().Be(SecureHospitalApiFactory.AdminUserName);
        session.Role.Should().Be(ApplicationRoles.Administrator);
        session.MedicalWorkerId.Should().BeNull();
    }

    [Fact]
    public async Task LoginAndMutate_MissingAntiforgeryToken_ShouldRejectRequest()
    {
        using var client = factory.CreateCookieClient();
        var credentials = factory.AdministratorLogin();
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await LoginAsync(client, credentials);
        client.DefaultRequestHeaders.Remove(CsrfHeader);

        var missing = await client.PostAsJsonAsync("/api/diagnoses", new { code = "CSRF01", name = "Защита CSRF" });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Add(CsrfHeader, "invalid-antiforgery-token");
        var forged = await client.PostAsJsonAsync("/api/diagnoses", new { code = "CSRF02", name = "Поддельный токен" });
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Send_AuthenticatedRole_ShouldAllowOnlyPermittedOperations()
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));

        (await administrator.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinician.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinician.GetAsync("/api/diagnoses")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clinician.GetAsync("/api/medical-workers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clinician.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clinician.PostAsJsonAsync("/api/diagnoses", new { code = "ROLE01", name = "Запрещённая запись" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MutateRecord_ForgedAuthor_ShouldUseAuthenticatedWorkerIdentity()
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        var other = await CreateWorkerAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));
        var createdPatient = await clinician.PostAsJsonAsync("/api/patients", new SavePatientRequest("Елена", "Пациент",
            null, new DateOnly(2000, 1, 1), AdministrativeSex.Female, null));
        createdPatient.EnsureSuccessStatusCode();
        var patient = (await createdPatient.Content.ReadFromJsonAsync<PatientDto>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/patients/{patient.Id}/medical-record");
        request.Headers.Add("X-Medical-Worker-Id", other.Id.ToString());

        var forged = await clinician.SendAsync(request);

        forged.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = (await forged.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
        record.PatientId.Should().Be(patient.Id);
        var history = (await clinician.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
            $"/api/medical-records/{record.Id}/history"))!;
        history.Should().ContainSingle().Which.PerformedByMedicalWorkerId.Should()
            .Be(doctor.Account.MedicalWorkerId.GetValueOrDefault());
    }

    [Fact]
    public async Task Logout_AuthenticatedSession_ShouldRemoveAccess()
    {
        using var client = await CreateAdminClientAsync();

        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteWorker_ExistingSession_ShouldRevokeAccess()
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));
        (await clinician.GetAsync("/api/patients")).EnsureSuccessStatusCode();

        (await administrator.DeleteAsync($"/api/medical-workers/{doctor.Account.MedicalWorkerId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await clinician.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await clinician.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateAccount_ChangedRoleOrWorkerBinding_ShouldRevokeStaleAccess(bool changeRole)
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        var other = await CreateWorkerAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            if (changeRole)
            {
                var binding = await db.UserRoles.SingleAsync(item => item.UserId == doctor.Account.Id);
                db.UserRoles.Remove(binding);
            }
            else
            {
                var user = await db.Users.SingleAsync(item => item.Id == doctor.Account.Id);
                user.MedicalWorkerId = other.Id;
            }
            await db.SaveChangesAsync();
        }

        (await clinician.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await clinician.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_IncorrectPasswords_ShouldUseHashesAndLockOutAccount()
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Set<HospitalUser>().AsNoTracking().SingleAsync(x => x.Id == doctor.Account.Id);
        user.PasswordHash.Should().NotBeNullOrWhiteSpace().And.NotBe(doctor.GeneratedPassword);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
        (await users.CheckPasswordAsync(user, doctor.GeneratedPassword)).Should().BeTrue();
        using var client = factory.CreateCookieClient();
        await RefreshCsrfAsync(client);
        for (var attempt = 0; attempt < users.Options.Lockout.MaxFailedAccessAttempts; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(doctor.Account.UserName, "Wrong-password!"));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await db.Set<HospitalUser>().AsNoTracking().SingleAsync(x => x.Id == doctor.Account.Id);
        locked.LockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow);
        var correctButLocked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));
        correctButLocked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdatePasswordOrStamp_ExistingSession_ShouldRevokeAccessImmediately(bool changePassword)
    {
        using var administrator = await CreateAdminClientAsync();
        var doctor = await CreateDoctorAccountAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(doctor.Account.UserName, doctor.GeneratedPassword));
        (await clinician.GetAsync("/api/patients")).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
            var user = (await users.FindByIdAsync(doctor.Account.Id.ToString()))!;
            var result = changePassword
                ? await users.ChangePasswordAsync(user, doctor.GeneratedPassword, "Updated.Doctor-Password.6249!")
                : await users.UpdateSecurityStampAsync(user);
            result.Succeeded.Should().BeTrue();
        }

        (await clinician.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await clinician.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var freshClient = factory.CreateCookieClient();
        await LoginAsync(freshClient, new LoginRequest(doctor.Account.UserName,
            changePassword ? "Updated.Doctor-Password.6249!" : doctor.GeneratedPassword));
        (await freshClient.GetAsync("/api/patients")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Send_AuthenticatedRequest_ShouldPreserveFixedAccessExpiration()
    {
        using var client = await CreateAdminClientAsync();

        factory.AdvanceTime(TimeSpan.FromMinutes(4));
        (await client.GetAsync("/api/accounts")).EnsureSuccessStatusCode();
        factory.AdvanceTime(TimeSpan.FromMinutes(2));

        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Send_ProductionPlainHttp_ShouldRejectAnonymousEndpoints()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureServices(services => services.PostConfigure<JwtTokenOptions>(options => options.AllowEphemeralKeys = true)));
        using var client = productionFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Login_ProductionHttps_ShouldUseHostOnlySecureCookies()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureServices(services => services.PostConfigure<JwtTokenOptions>(options => options.AllowEphemeralKeys = true)));
        using var client = productionFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        using var csrf = await client.GetAsync("/api/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        var antiforgery = (await csrf.Content.ReadFromJsonAsync<AntiforgeryTokenDto>())!;
        client.DefaultRequestHeaders.Add(CsrfHeader, antiforgery.RequestToken);

        using var response = await client.PostAsJsonAsync("/api/auth/login",
            factory.AdministratorLogin());

        response.EnsureSuccessStatusCode();
        var antiforgeryCookie = csrf.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("__Host-MedSoft.Antiforgery=", StringComparison.Ordinal));
        var refreshCookie = response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("__Host-MedSoft.Refresh=", StringComparison.Ordinal));
        foreach (var cookie in new[] { antiforgeryCookie, refreshCookie })
        {
            cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("path=/");
            cookie.Should().NotContain("domain=");
        }
    }

    [Fact]
    public async Task Login_ExcessAttempts_ShouldRateLimitBeforeAuthentication()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Security:LoginRateLimit:PermitLimit", "2"));
        using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        await RefreshCsrfAsync(client);
        var credentials = new LoginRequest("missing." + Guid.NewGuid().ToString("N")[..8], "Invalid.Password.123!");

        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Send_OversizedJson_ShouldRejectBeforeWritingData()
    {
        using var administrator = await CreateAdminClientAsync();
        var before = await administrator.GetStringAsync("/api/diagnoses");

        var response = await administrator.PostAsJsonAsync("/api/diagnoses",
            new { code = "LIMIT1", name = new string('x', 65 * 1024) });

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await administrator.GetStringAsync("/api/diagnoses")).Should().Be(before);
    }

    [Fact]
    public async Task Send_CrossOriginRequest_ShouldAllowOnlyConfiguredOriginsWithCredentials()
    {
        const string allowedOrigin = "https://clinical.example";
        using var corsFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Cors:AllowedOrigins:0", allowedOrigin));
        using var client = corsFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        allowed.Headers.Add("Origin", allowedOrigin);

        var accepted = await client.SendAsync(allowed);

        accepted.EnsureSuccessStatusCode();
        accepted.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(allowedOrigin);
        accepted.Headers.GetValues("Access-Control-Allow-Credentials").Should().Contain("true");
        using var forbidden = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        forbidden.Headers.Add("Origin", "https://attacker.example");
        var rejected = await client.SendAsync(forbidden);
        rejected.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        rejected.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = factory.CreateCookieClient();
        await LoginAsync(client,
            factory.AdministratorLogin());
        return client;
    }

    private static async Task LoginAsync(HttpClient client, LoginRequest credentials)
    {
        await RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).EnsureSuccessStatusCode();
        await RefreshCsrfAsync(client);
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        token.RequestToken.Should().NotBeNullOrWhiteSpace();
        client.DefaultRequestHeaders.Remove(CsrfHeader);
        client.DefaultRequestHeaders.Add(CsrfHeader, token.RequestToken);
    }

    private static async Task<CreateWorkerAccountResultDto> CreateDoctorAccountAsync(HttpClient administrator)
    {
        var worker = await CreateWorkerAsync(administrator);
        var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account", new CreateWorkerAccountRequest(
            "doctor." + Guid.NewGuid().ToString("N")[..9]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }

    private static async Task<MedicalWorkerDto> CreateWorkerAsync(HttpClient administrator)
    {
        var response = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Алексей", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
    }

}
