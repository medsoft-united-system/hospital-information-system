using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class SecureHospitalApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUserName = "it.admin";
    public const string AdminPassword = "Integration.Admin-Only.6249!";
    private readonly string _securityDirectory = Path.Combine(Path.GetTempPath(), "medsoft-test-security-" + Guid.NewGuid().ToString("N"));
    private readonly string _ownerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _certificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SecurityTestTimeProvider _clock = new();
    private string _administratorSecret = "";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("hospital_security_tests").WithUsername("postgres").WithPassword("postgres").Build();

    public SecureHospitalApiFactory()
    {
        Directory.CreateDirectory(_securityDirectory);
        using var jwt = RSA.Create(3072);
        File.WriteAllText(Path.Combine(_securityDirectory, "jwt-private.pem"), jwt.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_securityDirectory, "jwt-public.pem"), jwt.ExportSubjectPublicKeyInfoPem());
        using var wrappingKey = RSA.Create(3072);
        var request = new CertificateRequest("CN=MedSoft test key protection", wrappingKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(Path.Combine(_securityDirectory, "protection.pfx"), certificate.Export(X509ContentType.Pfx, _certificatePassword));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrations", "true");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Authentication:Setup:AdministratorUserName", AdminUserName);
        builder.UseSetting("Authentication:Setup:TokenSha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_ownerToken))));
        builder.UseSetting("Authentication:Jwt:PrivateKeyPath", Path.Combine(_securityDirectory, "jwt-private.pem"));
        builder.UseSetting("Authentication:Jwt:PublicKeyPath", Path.Combine(_securityDirectory, "jwt-public.pem"));
        builder.UseSetting("Security:DataProtection:KeyDirectory", Path.Combine(_securityDirectory, "keyring"));
        builder.UseSetting("Security:DataProtection:CertificatePath", Path.Combine(_securityDirectory, "protection.pfx"));
        builder.UseSetting("Security:DataProtection:CertificatePassword", _certificatePassword);
        builder.UseSetting("Security:LoginRateLimit:PermitLimit", "1000");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        });
    }

    public HttpClient CreateCookieClient() => CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());

    public LoginRequest AdministratorLogin()
    {
        AdvanceTime(TimeSpan.FromSeconds(31));
        return new LoginRequest(AdminUserName, AdminPassword,
            new TotpCodeService().GetCode(_administratorSecret, _clock.GetUtcNow()));
    }

    public void AdvanceTime(TimeSpan amount) => _clock.Advance(amount);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        using var client = CreateCookieClient();
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
        client.DefaultRequestHeaders.Add("X-Setup-Token", _ownerToken);
        using var response = await client.PostAsJsonAsync("/api/auth/setup/begin", new BeginSetupRequest(AdminPassword, AdminPassword));
        response.EnsureSuccessStatusCode();
        var challenge = (await response.Content.ReadFromJsonAsync<SetupChallengeDto>())!;
        _administratorSecret = challenge.SecretKey;
        using var completed = await client.PostAsJsonAsync("/api/auth/setup/confirm", new ConfirmSetupRequest(
            challenge.ChallengeId, new TotpCodeService().GetCode(_administratorSecret, _clock.GetUtcNow())));
        completed.EnsureSuccessStatusCode();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        Directory.Delete(_securityDirectory, recursive: true);
    }
}
