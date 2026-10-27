using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class StartupRoleApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    private const string OwnerToken = "externally-migrated-database-owner-token-for-tests";
    private const string AdministratorPassword = "External.Migration-Only.6249!";

    [Fact]
    public async Task Start_ExternallyMigratedDatabase_ShouldPrepareRolesAndAllowMedicalAccounts()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using (var database = new HospitalDbContext(options))
        {
            await database.Database.MigrateAsync(CancellationToken.None);
            (await database.Roles.AnyAsync(CancellationToken.None)).Should().BeFalse();
        }

        Dictionary<string, Guid> roleIds;
        using (var host = NewHost(postgres))
        {
            using var client = host.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                roleIds = await database.Roles.ToDictionaryAsync(role => role.Name!, role => role.Id,
                    CancellationToken.None);
                roleIds.Keys.Should().BeEquivalentTo(ApplicationRoles.Administrator, ApplicationRoles.MedicalWorker);
                (await database.Users.AnyAsync(CancellationToken.None)).Should().BeFalse();
            }

            await SetCsrfAsync(client, CancellationToken.None);
            client.DefaultRequestHeaders.Add("X-Setup-Token", OwnerToken);
            using var begin = await client.PostAsJsonAsync("/api/auth/setup/begin",
                new BeginSetupRequest(AdministratorPassword, AdministratorPassword), CancellationToken.None);
            begin.StatusCode.Should().Be(HttpStatusCode.OK);
            var challenge = (await begin.Content.ReadFromJsonAsync<SetupChallengeDto>(CancellationToken.None))!;
            var time = host.Services.GetRequiredService<TimeProvider>();
            var code = new TotpCodeService().GetCode(challenge.SecretKey, time.GetUtcNow());
            using var confirmed = await client.PostAsJsonAsync("/api/auth/setup/confirm",
                new ConfirmSetupRequest(challenge.ChallengeId, code), CancellationToken.None);
            confirmed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            client.DefaultRequestHeaders.Remove("X-Setup-Token");

            factory.AdvanceTime(TimeSpan.FromSeconds(31));
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(SecureHospitalApiFactory.AdminUserName, AdministratorPassword,
                    new TotpCodeService().GetCode(challenge.SecretKey, time.GetUtcNow())), CancellationToken.None);
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            await SetCsrfAsync(client, CancellationToken.None);
            using var createdWorker = await client.PostAsJsonAsync("/api/medical-workers",
                new SaveMedicalWorkerRequest("Врач", "Внешний", "Врач", MedicalWorkerRole.Doctor),
                CancellationToken.None);
            createdWorker.StatusCode.Should().Be(HttpStatusCode.Created);
            var worker = (await createdWorker.Content.ReadFromJsonAsync<MedicalWorkerDto>(CancellationToken.None))!;
            using var createdAccount = await client.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
                new CreateWorkerAccountRequest("external.doctor"), CancellationToken.None);
            createdAccount.StatusCode.Should().Be(HttpStatusCode.Created);
            var account = (await createdAccount.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>(CancellationToken.None))!;
            account.Account.AccountRole.Should().Be(ApplicationRoles.MedicalWorker);
        }

        using var restarted = NewHost(postgres);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedDatabase = restartedScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await restartedDatabase.Roles.ToDictionaryAsync(role => role.Name!, role => role.Id, CancellationToken.None))
            .Should().BeEquivalentTo(roleIds);
        (await restartedDatabase.Users.CountAsync(user => user.IsAdministrator, CancellationToken.None)).Should().Be(1);
    }

    private WebApplicationFactory<Program> NewHost(PostgreSqlContainer postgres) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString());
            builder.UseSetting("Database:ApplyMigrations", "false");
            builder.UseSetting("Database:Seed", "false");
            builder.UseSetting("Authentication:Setup:TokenSha256", Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(OwnerToken))));
        });

    private static async Task SetCsrfAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf", cancellationToken))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
