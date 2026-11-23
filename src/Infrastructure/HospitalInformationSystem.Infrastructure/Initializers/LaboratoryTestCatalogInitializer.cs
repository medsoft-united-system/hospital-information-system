using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Initializers;

public sealed class LaboratoryTestCatalogInitializer(HospitalDbContext db, TimeProvider timeProvider)
{
    private static readonly IReadOnlyList<SeedDefinition> SeedDefinitions = Array.AsReadOnly<SeedDefinition>(
    [
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e01"), new("2345-7", "Глюкоза (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2345-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e02"), new("718-7", "Гемоглобин (массовая концентрация) в крови", "https://loinc.org/718-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e03"), new("7918-6", "ВИЧ-1/2: антитела в сыворотке", "https://loinc.org/7918-6/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e04"), new("5196-1", "Гепатит B: HBsAg в сыворотке или плазме (иммуноанализ)", "https://loinc.org/5196-1/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e05"), new("13955-0", "Гепатит C: антитела в сыворотке или плазме (иммуноанализ)", "https://loinc.org/13955-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e06"), new("5393-4", "Сифилис: антитела к Treponema pallidum в сыворотке (иммунофлуоресценция)", "https://loinc.org/5393-4/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e07"), new("101363-0", "Чума: ДНК Yersinia pestis в крови (амплификация)", "https://loinc.org/101363-0/"))
    ]);

    public static IReadOnlyList<LaboratoryTestCatalogEntry> Definitions { get; } =
        Array.AsReadOnly(SeedDefinitions.Select(x => x.Definition).ToArray());

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        foreach (var seed in SeedDefinitions)
        {
            var definition = seed.Definition;
            var entity = LaboratoryTestDefinitionFactory.Create(definition.Code, definition.DisplayName, now);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO laboratory_test_definitions
                    (id, loinc_code, display_name, is_active, created_at_utc, updated_at_utc)
                VALUES ({seed.Id}, {entity.LoincCode}, {entity.DisplayName}, {entity.IsActive},
                    {entity.CreatedAtUtc}, {entity.UpdatedAtUtc})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
        }
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private sealed record SeedDefinition(Guid Id, LaboratoryTestCatalogEntry Definition);
}
