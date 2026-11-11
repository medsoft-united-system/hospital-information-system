using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryTestsApiTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    private readonly HttpClient _client = factory.CreateBusinessClient();

    [Fact]
    public async Task ManageCatalog_ShouldNormalizePersistAndDeactivateWithoutDeleting()
    {
        var code = NewCode();
        const string name = "Лабораторное исследование с названием длиннее двадцати четырёх символов";
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(" " + code + " ", " " + name + " "));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definition = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        definition.Id.Should().NotBeEmpty();
        definition.LoincCode.Should().Be(code);
        definition.DisplayName.Should().Be(name);
        definition.IsActive.Should().BeTrue();
        definition.Version.Should().BeGreaterThan(0);
        definition.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        definition.UpdatedAtUtc.Should().Be(definition.CreatedAtUtc);
        created.Headers.Location!.ToString().Should().EndWith($"/api/laboratory-tests/{definition.Id}");
        (await _client.GetAsync(created.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);

        var renamedCode = NewCode();
        using var updated = await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}",
            new SaveLaboratoryTestRequest(" " + renamedCode + " ", " Изменённое исследование ", definition.Version));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = (await updated.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        saved.Id.Should().Be(definition.Id);
        saved.CreatedAtUtc.Should().BeCloseTo(definition.CreatedAtUtc, TimeSpan.FromMicroseconds(1));
        saved.UpdatedAtUtc.Should().BeOnOrAfter(saved.CreatedAtUtc);
        saved.LoincCode.Should().Be(renamedCode);
        saved.DisplayName.Should().Be("Изменённое исследование");
        saved.Version.Should().BeGreaterThan(0).And.NotBe(definition.Version);

        using var disabled = await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, saved.Version));
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!
            .Should().NotContain(x => x.Id == definition.Id);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests?activeOnly=true"))!
            .Should().NotContain(x => x.Id == definition.Id);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests?activeOnly=false"))!
            .Should().Contain(x => x.Id == definition.Id && !x.IsActive);
        var inactive = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(
            $"/api/laboratory-tests/{definition.Id}"))!;
        inactive.IsActive.Should().BeFalse();
        inactive.LoincCode.Should().Be(renamedCode);
        inactive.Version.Should().BeGreaterThan(0).And.NotBe(saved.Version);

        using var sameState = await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, inactive.Version));
        sameState.StatusCode.Should().Be(HttpStatusCode.OK);
        var unchanged = (await sameState.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        unchanged.UpdatedAtUtc.Should().Be(inactive.UpdatedAtUtc);
        unchanged.Version.Should().Be(inactive.Version);
        (await _client.DeleteAsync($"/api/laboratory-tests/{definition.Id}")).StatusCode.Should()
            .Be(HttpStatusCode.MethodNotAllowed);

        using var reactivated = await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(true, inactive.Version));
        reactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!
            .Should().Contain(x => x.Id == definition.Id && x.IsActive);
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().LaboratoryTestDefinitions
            .AsNoTracking().SingleAsync(x => x.Id == definition.Id);
        row.LoincCode.Should().Be(renamedCode);
        row.DisplayName.Should().Be(saved.DisplayName);
        row.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Save_DuplicateTrimmedCode_ShouldConflictAndKeepExistingData()
    {
        var code = NewCode();
        var otherCode = NewCode();
        const string name = "Одинаковое название допустимо для разных кодов";
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(code, name));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var first = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        using var secondResponse = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(otherCode, name));
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = (await secondResponse.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        using var duplicate = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(" " + code + " ", "Другая запись"));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadFromJsonAsync<ProblemDetails>())!.Status.Should().Be(409);
        (await _client.PutAsJsonAsync($"/api/laboratory-tests/{second.Id}",
            new SaveLaboratoryTestRequest(" " + code + " ", "Новое название", second.Version))).StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"/api/laboratory-tests/{second.Id}"))!
            .LoincCode.Should().Be(otherCode);
        (await _client.PutAsJsonAsync($"/api/laboratory-tests/{first.Id}",
            new SaveLaboratoryTestRequest(code, name, first.Version))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_StaleForms_ShouldConflictWithoutOverwritingNewerNameCodeOrActivity()
    {
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(NewCode(), "Исходный анализ"));
        created.EnsureSuccessStatusCode();
        var original = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        var url = $"/api/laboratory-tests/{original.Id}";
        var openForm = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(url))!;
        openForm.Version.Should().Be(original.Version);
        var newCode = NewCode();
        using var updated = await _client.PutAsJsonAsync(url,
            new SaveLaboratoryTestRequest(newCode, "Актуальное название", original.Version));
        updated.EnsureSuccessStatusCode();
        var current = (await updated.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        current.Version.Should().NotBe(openForm.Version);

        using var staleEdit = await _client.PutAsJsonAsync(url,
            new SaveLaboratoryTestRequest(openForm.LoincCode, "Устаревшее изменение", openForm.Version));
        staleEdit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleEdit.Content.ReadFromJsonAsync<ProblemDetails>())!.Status.Should().Be(409);
        foreach (var isActive in new[] { true, false })
        {
            using var staleActivity = await _client.PutAsJsonAsync(url + "/active",
                new SetLaboratoryTestActiveRequest(isActive, openForm.Version));
            staleActivity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(url)).Should().BeEquivalentTo(current);

        using var disabled = await _client.PutAsJsonAsync(url + "/active",
            new SetLaboratoryTestActiveRequest(false, current.Version));
        disabled.EnsureSuccessStatusCode();
        var inactive = (await disabled.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        using var staleAfterActivity = await _client.PutAsJsonAsync(url,
            new SaveLaboratoryTestRequest(current.LoincCode, "Изменение после деактивации", current.Version));
        staleAfterActivity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(url)).Should().BeEquivalentTo(inactive);
    }

    [Fact]
    public async Task Save_MissingOrZeroVersion_ShouldRejectWithoutChangingCatalogEntry()
    {
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(NewCode(), "Анализ для проверки версии"));
        created.EnsureSuccessStatusCode();
        var original = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        var url = $"/api/laboratory-tests/{original.Id}";
        foreach (var body in new object[]
                 {
                     new { original.LoincCode, DisplayName = "Без версии" },
                     new SaveLaboratoryTestRequest(original.LoincCode, "Нулевая версия", 0)
                 })
        {
            using var response = await _client.PutAsJsonAsync(url, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        foreach (var body in new object[]
                 {
                     new { IsActive = false },
                     new SetLaboratoryTestActiveRequest(false, 0)
                 })
        {
            using var response = await _client.PutAsJsonAsync(url + "/active", body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(url)).Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(null, "Анализ")]
    [InlineData("", "Анализ")]
    [InlineData(" ", "Анализ")]
    [InlineData("1", "Анализ")]
    [InlineData("2345", "Анализ")]
    [InlineData("2345-X", "Анализ")]
    [InlineData("2345-8", "Анализ")]
    [InlineData("2345-77", "Анализ")]
    [InlineData("٢٣٤٥-٧", "Анализ")]
    [InlineData("2345-7\nother", "Анализ")]
    [InlineData("2345-7", null)]
    [InlineData("2345-7", " АБ ")]
    [InlineData("2345-7", " ")]
    [InlineData("2345-7", "Имя\0анализ")]
    public async Task Create_InvalidFields_ShouldReturnBadRequestWithoutInsert(string? code, string? name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var before = await db.LaboratoryTestDefinitions.CountAsync();
        using var response = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new { LoincCode = code, DisplayName = name });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await db.LaboratoryTestDefinitions.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Save_BoundaryLengths_ShouldCountUnicodeScalarsAndRejectOverlongFields()
    {
        var code = NewCode();
        var name = string.Concat(Enumerable.Repeat("🧪", 256));
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(code, name));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definition = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        foreach (var invalid in new[]
                 {
                     new SaveLaboratoryTestRequest(code, name + "Я", definition.Version),
                     new SaveLaboratoryTestRequest(new string('1', 31) + "-1", "Анализ", definition.Version)
                 })
        {
            (await _client.PostAsJsonAsync("/api/laboratory-tests", invalid)).StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
            (await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}", invalid)).StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
        }
        var saved = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>(
            $"/api/laboratory-tests/{definition.Id}"))!;
        saved.LoincCode.Should().Be(code);
        saved.DisplayName.Should().Be(name);
    }

    [Fact]
    public async Task SetActive_MissingOrInvalidFlag_ShouldRejectWithoutDeactivating()
    {
        using var created = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(NewCode(), "Проверка обязательного флага"));
        created.EnsureSuccessStatusCode();
        var definition = (await created.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        foreach (var body in new object[] { new { }, new { IsActive = (bool?)null }, new { IsActive = "false" } })
            (await _client.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}/active", body)).StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"/api/laboratory-tests/{definition.Id}"))!
            .IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("ACTIVE")]
    public async Task Send_UnknownId_ShouldReturnNotFoundProblem(string operation)
    {
        var url = $"/api/laboratory-tests/{Guid.NewGuid()}";
        using var response = operation switch
        {
            "GET" => await _client.GetAsync(url),
            "PUT" => await _client.PutAsJsonAsync(url, new SaveLaboratoryTestRequest(NewCode(), "Анализ", 1)),
            _ => await _client.PutAsJsonAsync(url + "/active", new SetLaboratoryTestActiveRequest(false, 1))
        };
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status.Should().Be(404);
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
}
