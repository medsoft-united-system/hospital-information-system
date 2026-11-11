using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.Extensions.DependencyInjection;
using WorkerRole = HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryTestsAuthorizationApiTests(SecureHospitalApiFactory factory)
    : IClassFixture<SecureHospitalApiFactory>
{
    [Theory]
    [InlineData(WorkerRole.Doctor)]
    [InlineData(WorkerRole.Nurse)]
    [InlineData(WorkerRole.LaboratoryTechnician)]
    [InlineData(WorkerRole.Other)]
    public async Task MedicalWorker_ShouldReadCatalogButNeverManageIt(WorkerRole role)
    {
        using var client = await CreateClinicianAsync(role);
        var definitions = (await client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>(
            "/api/laboratory-tests?activeOnly=false"))!;
        definitions.Should().Contain(x => x.LoincCode == "2345-7");
        var definition = definitions.First(x => x.LoincCode == "2345-7");
        (await client.GetAsync($"/api/laboratory-tests/{definition.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest("987654321-7", "Запрещённое создание"))).StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}",
            new SaveLaboratoryTestRequest(definition.LoincCode, "Запрещённое изменение", definition.Version))).StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, definition.Version))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"/api/laboratory-tests/{definition.Id}"))
            .Should().BeEquivalentTo(definition);
    }

    [Fact]
    public async Task Anonymous_ShouldNotReadOrWriteCatalogEvenWithValidCsrf()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        var missing = Guid.NewGuid();
        (await client.GetAsync("/api/laboratory-tests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"/api/laboratory-tests/{missing}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest("987654322-5", "Недоступная запись"))).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{missing}",
            new SaveLaboratoryTestRequest("987654322-5", "Недоступная запись", 1))).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{missing}/active",
            new SetLaboratoryTestActiveRequest(false, 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Administrator_ShouldManageCatalogAndStillRequireCsrfForEveryMutation()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        const string code = "987654323-3";
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        (await client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(code, "Проверка CSRF"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await SetCsrfAsync(client);
        using var created = await client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(code, "Проверка CSRF"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definition = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}",
            new SaveLaboratoryTestRequest(code, "Изменение без CSRF", definition.Version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, definition.Version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"/api/laboratory-tests/{definition.Id}"))!
            .DisplayName.Should().Be("Проверка CSRF");

        await SetCsrfAsync(client);
        using var updated = await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}",
            new SaveLaboratoryTestRequest(code, "Обновлённый анализ", definition.Version));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = (await updated.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        (await client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, current.Version))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests?activeOnly=false"))!
            .Should().Contain(x => x.Id == definition.Id && !x.IsActive && x.DisplayName == "Обновлённый анализ");
        (await client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!
            .Should().NotContain(x => x.Id == definition.Id);
    }

    private async Task<HttpClient> CreateClinicianAsync(WorkerRole role)
    {
        var client = factory.CreateCookieClient();
        const string password = "Catalog.Matrix-6249!";
        var userName = "lab." + Guid.NewGuid().ToString("N")[..10];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var worker = new MedicalWorkerDbo
            {
                Id = Guid.NewGuid(), FirstName = "Проверка", LastName = "Справочника", Position = "Сотрудник",
                Role = role, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.MedicalWorkers.Add(worker);
            await db.SaveChangesAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
                new CreateAccountRequest(userName, password, ApplicationRoles.MedicalWorker, worker.Id),
                CancellationToken.None);
        }
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password))).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        return client;
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
