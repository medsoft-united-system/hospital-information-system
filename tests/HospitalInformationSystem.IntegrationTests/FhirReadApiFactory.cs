using System.Net.Http.Headers;
using System.Security.Cryptography;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirReadApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string FhirBase = "https://his-api:5201/fhir";
    public static readonly Guid ServiceId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    public Guid PatientId { get; } = Guid.NewGuid();
    public Guid PractitionerId { get; } = Guid.NewGuid();
    public Guid RecordId { get; private set; }
    public Guid CompletedRequestId { get; private set; }
    public Guid ObservationId { get; private set; }
    public IReadOnlyList<Guid> RequestIds { get; private set; } = [];
    public static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("hospital_fhir_read").WithUsername("postgres").WithPassword("postgres").Build();

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
        });
    }

    public HttpClient CreateServiceClient(string credential = "lis")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://his-api:5201"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("FhirTest", credential);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+json");
        return client;
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Patients.Add(new PatientDbo
        {
            Id = PatientId,
            FirstName = "Алексей",
            LastName = "Петров",
            MiddleName = "Андреевич",
            BirthDate = new DateOnly(1985, 3, 12),
            Sex = 0,
            Comment = "PRIVATE-PATIENT-COMMENT",
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        });
        var doctor = new MedicalWorker
        {
            Id = PractitionerId,
            FirstName = "Иван",
            LastName = "Иванов",
            Position = "Врач-терапевт",
            Role = MedicalWorkerRole.Doctor,
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        };
        db.MedicalWorkers.Add(new MedicalWorkerDbo
        {
            Id = doctor.Id,
            FirstName = doctor.FirstName,
            LastName = doctor.LastName,
            Position = doctor.Position,
            Role = doctor.Role,
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        });
        db.ServiceAccounts.Add(new ServiceAccountDbo
        {
            Id = ServiceId,
            Name = "lis.read-tests",
            NormalizedName = "LIS.READ-TESTS",
            ApiKeyHash = RandomNumberGenerator.GetBytes(32),
            IsActive = true,
            AuthenticationVersion = 1,
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        });
        await db.SaveChangesAsync();
        var row = await db.LaboratoryTestDefinitions.AsNoTracking().SingleAsync(x => x.LoincCode == "2345-7");
        var definition = new LaboratoryTestDefinition
        {
            Id = row.Id,
            LoincCode = row.LoincCode,
            DisplayName = row.DisplayName,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc
        };
        var record = MedicalRecordFactory.Create(PatientId, doctor, At);
        for (var index = 0; index < 25; index++)
            MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], "Комментарий к анализу",
                doctor, At.AddMinutes(1).AddTicks(index * 10));
        var completed = record.LaboratoryAnalysisRequests.First();
        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, completed.Id, PatientId,
            completed.LoincCodeSnapshot, "5.6", At.AddMinutes(2), ServiceId, "lis.read-tests", At.AddMinutes(3));
        scope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>().Add(record);
        await db.SaveChangesAsync();
        RecordId = record.Id;
        CompletedRequestId = completed.Id;
        ObservationId = result.Id;
        RequestIds = record.LaboratoryAnalysisRequests.Select(x => x.Id).ToArray();
        var workerRow = await db.MedicalWorkers.SingleAsync(x => x.Id == PractitionerId);
        workerRow.IsDeleted = true;
        var catalogRow = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == definition.Id);
        catalogRow.DisplayName = "Название после назначения";
        catalogRow.IsActive = false;
        catalogRow.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
