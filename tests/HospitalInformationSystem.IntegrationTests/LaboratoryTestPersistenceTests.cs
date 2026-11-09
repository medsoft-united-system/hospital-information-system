using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
using HospitalInformationSystem.Domain.LaboratoryTests.Services;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Initializers;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryTestPersistenceTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    [Fact]
    public async Task InitializeWithoutDemoSeed_ShouldRestoreMissingCatalogEntryAndPreserveUserChanges()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var defaults = LaboratoryTestCatalogInitializer.Definitions;
        var changed = await db.LaboratoryTestDefinitions.SingleAsync(x => x.LoincCode == defaults[0].Code);
        changed.DisplayName = "Название, изменённое администратором";
        changed.IsActive = false;
        changed.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var changedId = changed.Id;
        var changedAt = changed.UpdatedAtUtc;
        var missingCode = defaults[1].Code;
        await db.LaboratoryTestDefinitions.Where(x => x.LoincCode == missingCode).ExecuteDeleteAsync();
        var custom = NewDefinition();
        db.LaboratoryTestDefinitions.Add(custom);
        await db.SaveChangesAsync();
        var workersBefore = await db.MedicalWorkers.CountAsync();
        var diagnosesBefore = await db.DiagnosisDefinitions.CountAsync();

        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(seed: false, CancellationToken.None);
        await initializer.InitializeAsync(seed: false, CancellationToken.None);
        db.ChangeTracker.Clear();

        var saved = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == changedId);
        saved.DisplayName.Should().Be("Название, изменённое администратором");
        saved.IsActive.Should().BeFalse();
        saved.UpdatedAtUtc.Should().BeCloseTo(changedAt, TimeSpan.FromMicroseconds(1));
        (await db.LaboratoryTestDefinitions.CountAsync(x => x.LoincCode == missingCode)).Should().Be(1);
        (await db.LaboratoryTestDefinitions.AnyAsync(x => x.Id == custom.Id)).Should().BeTrue();
        (await db.MedicalWorkers.CountAsync()).Should().Be(workersBefore);
        (await db.DiagnosisDefinitions.CountAsync()).Should().Be(diagnosesBefore);
        foreach (var definition in defaults)
            (await db.LaboratoryTestDefinitions.CountAsync(x => x.LoincCode == definition.Code)).Should().Be(1);
    }

    [Fact]
    public async Task CatalogInitialization_ConcurrentScopes_ShouldNotDuplicateDefaults()
    {
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var code = LaboratoryTestCatalogInitializer.Definitions[^1].Code;
        await firstDb.LaboratoryTestDefinitions.Where(x => x.LoincCode == code).ExecuteDeleteAsync();

        await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<LaboratoryTestCatalogInitializer>().InitializeAsync(CancellationToken.None),
            secondScope.ServiceProvider.GetRequiredService<LaboratoryTestCatalogInitializer>().InitializeAsync(CancellationToken.None));

        (await firstDb.LaboratoryTestDefinitions.CountAsync(x => x.LoincCode == code)).Should().Be(1);
    }

    [Fact]
    public async Task CatalogInitialization_EditedSeedCode_ShouldNotRestoreOriginalOrLoseUserChanges()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var originalCode = LaboratoryTestCatalogInitializer.Definitions[2].Code;
        var edited = await db.LaboratoryTestDefinitions.SingleAsync(x => x.LoincCode == originalCode);
        var id = edited.Id;
        var count = await db.LaboratoryTestDefinitions.CountAsync();
        var newCode = NewDefinition().LoincCode;
        edited.LoincCode = newCode;
        edited.DisplayName = "Изменённые код и название исходного анализа";
        edited.IsActive = false;
        edited.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var updatedAt = edited.UpdatedAtUtc;
        await db.SaveChangesAsync();
        var initializer = scope.ServiceProvider.GetRequiredService<LaboratoryTestCatalogInitializer>();

        await initializer.InitializeAsync(CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.LaboratoryTestDefinitions.AnyAsync(x => x.LoincCode == originalCode)).Should().BeFalse();
        (await db.LaboratoryTestDefinitions.CountAsync()).Should().Be(count);
        var saved = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == id);
        saved.LoincCode.Should().Be(newCode);
        saved.DisplayName.Should().Be("Изменённые код и название исходного анализа");
        saved.IsActive.Should().BeFalse();
        saved.UpdatedAtUtc.Should().BeCloseTo(updatedAt, TimeSpan.FromMicroseconds(1));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Repository_ActiveSelection_ShouldHideInactiveDefinitionsButKeepThemReadable()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var inactive = NewDefinition();
        inactive.IsActive = false;
        db.LaboratoryTestDefinitions.Add(inactive);
        await db.SaveChangesAsync();
        var repository = scope.ServiceProvider.GetRequiredService<ILaboratoryTestRepository>();

        (await repository.ListAsync(true, CancellationToken.None)).Should().NotContain(x => x.Id == inactive.Id);
        (await repository.ListAsync(false, CancellationToken.None)).Should().Contain(x => x.Id == inactive.Id);
        var saved = await repository.FindAsync(inactive.Id, false, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.LoincCode.Should().Be(inactive.LoincCode);
        saved.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Repository_AddAndUpdate_ShouldMapMutableFieldsAndPreserveIdentity()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ILaboratoryTestRepository>();
        var now = DateTimeOffset.UtcNow;
        var definition = LaboratoryTestDefinitionFactory.Create(NewDefinition().LoincCode,
            "Анализ с Unicode " + string.Concat(Enumerable.Repeat("🧪", 130)), now);
        repository.Add(definition);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var loaded = await repository.FindAsync(definition.Id, true, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.LoincCode.Should().Be(definition.LoincCode);
        loaded.DisplayName.Should().Be(definition.DisplayName);
        loaded.IsActive.Should().BeTrue();
        loaded.CreatedAtUtc.Should().BeCloseTo(now, TimeSpan.FromMicroseconds(1));
        var updatedAt = now.AddMinutes(1);
        var updatedCode = NewDefinition().LoincCode;
        LaboratoryTestDefinitionService.Update(loaded, updatedCode, "Обновлённое название анализа", updatedAt);
        LaboratoryTestDefinitionService.SetActive(loaded, false, updatedAt);

        await repository.UpdateAsync(loaded, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == definition.Id);
        saved.Id.Should().Be(definition.Id);
        saved.LoincCode.Should().Be(updatedCode);
        saved.DisplayName.Should().Be("Обновлённое название анализа");
        saved.IsActive.Should().BeFalse();
        saved.CreatedAtUtc.Should().BeCloseTo(now, TimeSpan.FromMicroseconds(1));
        saved.UpdatedAtUtc.Should().BeCloseTo(updatedAt, TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public async Task Save_DuplicateLoincCode_ShouldConflictRegardlessOfActivity()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var first = NewDefinition();
        first.IsActive = false;
        db.LaboratoryTestDefinitions.Add(first);
        await db.SaveChangesAsync();
        db.LaboratoryTestDefinitions.Add(NewDefinition(first.LoincCode));

        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<BusinessConflictException>();
    }

    [Theory]
    [InlineData("loinc_code", "A00.00")]
    [InlineData("loinc_code", "2345-7 ")]
    [InlineData("display_name", "ab")]
    [InlineData("display_name", "   ")]
    public async Task Update_InvalidDirectDatabaseText_ShouldEnforceConstraints(string column, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var definition = NewDefinition();
        db.LaboratoryTestDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var sql = column switch
        {
            "loinc_code" => "UPDATE laboratory_test_definitions SET loinc_code = @value WHERE id = @id",
            "display_name" => "UPDATE laboratory_test_definitions SET display_name = @value WHERE id = @id",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

        var update = () => db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("value", value), new NpgsqlParameter("id", definition.Id));

        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Save_StaleDefinition_ShouldNotOverwriteConcurrentEdit()
    {
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var first = NewDefinition();
        firstDb.LaboratoryTestDefinitions.Add(first);
        await firstDb.SaveChangesAsync();
        var stale = await secondDb.LaboratoryTestDefinitions.SingleAsync(x => x.Id == first.Id);
        first.IsActive = false;
        await firstDb.SaveChangesAsync();
        stale.DisplayName = "Изменение из устаревшего контекста";

        var save = () => secondDb.SaveChangesAsync();

        await save.Should().ThrowAsync<BusinessConflictException>();
    }

    private static LaboratoryTestDefinitionDbo NewDefinition(string? code = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new LaboratoryTestDefinitionDbo
        {
            Id = Guid.NewGuid(),
            LoincCode = code ?? BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-0",
            DisplayName = "Синтетический анализ для проверки хранения",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}
