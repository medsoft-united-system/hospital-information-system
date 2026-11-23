using System.Net.Http.Headers;
using System.Security.Cryptography;
using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirObservationCreateApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string FhirBase = FhirReadApiFactory.FhirBase;
    public static readonly Guid ServiceId = FhirReadApiFactory.ServiceId;
    public static readonly DateTimeOffset CreatedAt = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset ReceivedAt = CreatedAt.AddMinutes(10);
    public MedicalWorker Doctor { get; } = MedicalWorkerFactory.Create("Иван", "Иванов", "Врач-терапевт",
        CreatedAt, MedicalWorkerRole.Doctor);
    public ConcurrentRecordReadGate RecordReads { get; } = new();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("hospital_fhir_create").WithUsername("postgres").WithPassword("postgres").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrations", "true");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Fhir:PublicBaseUrl", FhirBase);
        builder.UseSetting("AllowedHosts", "localhost;his-api;attacker.invalid");
        builder.ConfigureTestServices(services =>
        {
            services.AddTransient<FhirTestAuthenticationHandler>();
            services.Configure<AuthenticationOptions>(options =>
                options.SchemeMap[ServiceAuthenticationDefaults.Scheme].HandlerType = typeof(FhirTestAuthenticationHandler));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock());
            services.AddDbContext<HospitalDbContext>(options => options.AddInterceptors(RecordReads));
        });
    }

    public HttpClient CreateServiceClient(string? credential = "lis") => CreateServiceClient(this, credential);

    public static HttpClient CreateServiceClient(WebApplicationFactory<Program> application, string? credential = "lis")
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://his-api:5201"), AllowAutoRedirect = false
        });
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("FhirTest", credential);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+json");
        return client;
    }

    public async Task<Scenario> CreateScenarioAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var patient = new PatientDbo
        {
            Id = Guid.NewGuid(), FirstName = "Алексей", LastName = "Петров", MiddleName = "Андреевич",
            BirthDate = new DateOnly(1985, 3, 12), Sex = 0,
            CreatedAtUtc = CreatedAt, UpdatedAtUtc = CreatedAt
        };
        var definition = LaboratoryTestDefinitionFactory.Create(
            NewCode(),
            "Исследование, сохранённое в назначении", CreatedAt);
        db.Patients.Add(patient);
        db.LaboratoryTestDefinitions.Add(new LaboratoryTestDefinitionDbo
        {
            Id = definition.Id, LoincCode = definition.LoincCode, DisplayName = definition.DisplayName,
            IsActive = true, CreatedAtUtc = CreatedAt, UpdatedAtUtc = CreatedAt
        });
        await db.SaveChangesAsync();
        var record = MedicalRecordFactory.Create(patient.Id, Doctor, CreatedAt);
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition],
            "Комментарий к назначению", Doctor, CreatedAt.AddMinutes(1)).Single();
        scope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>().Add(record);
        await db.SaveChangesAsync();
        return new Scenario(patient.Id, record.Id, request.Id, definition.Id, definition.LoincCode,
            definition.DisplayName, record.Version);
    }

    private static string NewCode()
    {
        var number = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray())
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var index = number.Length - 1; index >= 0; index--)
        {
            var digit = number[index] - '0';
            if ((number.Length - index) % 2 == 1)
                digit *= 2;
            sum += digit / 10 + digit % 10;
        }
        return number + "-" + (10 - sum % 10) % 10;
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.MedicalWorkers.Add(new MedicalWorkerDbo
        {
            Id = Doctor.Id, FirstName = Doctor.FirstName, LastName = Doctor.LastName, Position = Doctor.Position,
            Role = Doctor.Role, CreatedAtUtc = CreatedAt, UpdatedAtUtc = CreatedAt
        });
        db.ServiceAccounts.Add(new ServiceAccountDbo
        {
            Id = ServiceId, Name = "lis.read-tests", NormalizedName = "LIS.READ-TESTS",
            ApiKeyHash = RandomNumberGenerator.GetBytes(32), IsActive = true,
            AuthenticationVersion = 1, CreatedAtUtc = CreatedAt, UpdatedAtUtc = CreatedAt
        });
        await db.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public sealed record Scenario(Guid PatientId, Guid RecordId, Guid RequestId, Guid DefinitionId,
        string LoincCode, string DisplayName, long InitialVersion);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ReceivedAt;
    }
}
