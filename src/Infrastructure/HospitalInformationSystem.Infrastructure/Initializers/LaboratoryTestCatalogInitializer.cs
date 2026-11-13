using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
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
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e07"), new("101363-0", "Чума: ДНК Yersinia pestis в крови (амплификация)", "https://loinc.org/101363-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e08"), new("6690-2", "Лейкоциты в крови (автоматический подсчёт)", "https://loinc.org/6690-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e09"), new("789-8", "Эритроциты в крови (автоматический подсчёт)", "https://loinc.org/789-8/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0a"), new("4544-3", "Гематокрит крови (объёмная доля, автоматический подсчёт)", "https://loinc.org/4544-3/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0b"), new("787-2", "Средний объём эритроцита (MCV, автоматический подсчёт)", "https://loinc.org/787-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0c"), new("785-6", "Среднее содержание гемоглобина в эритроците (MCH, автоматический подсчёт)", "https://loinc.org/785-6/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0d"), new("786-4", "Средняя концентрация гемоглобина в эритроцитах (MCHC, автоматический подсчёт)", "https://loinc.org/786-4/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0e"), new("788-0", "Ширина распределения эритроцитов по объёму (RDW-CV, автоматический подсчёт)", "https://loinc.org/788-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e0f"), new("777-3", "Тромбоциты в крови (автоматический подсчёт)", "https://loinc.org/777-3/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e10"), new("32623-1", "Средний объём тромбоцита (MPV, автоматический подсчёт)", "https://loinc.org/32623-1/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e11"), new("4537-7", "Скорость оседания эритроцитов (СОЭ, метод Вестергрена)", "https://loinc.org/4537-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e12"), new("1988-5", "С-реактивный белок (массовая концентрация) в сыворотке или плазме", "https://loinc.org/1988-5/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e13"), new("4548-4", "Гликированный гемоглобин HbA1c / общий гемоглобин в крови (массовая доля)", "https://loinc.org/4548-4/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e14"), new("3016-3", "Тиреотропный гормон (ТТГ) в сыворотке или плазме", "https://loinc.org/3016-3/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e15"), new("3024-7", "Тироксин свободный (Т4, массовая концентрация) в сыворотке или плазме", "https://loinc.org/3024-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e16"), new("3051-0", "Трийодтиронин свободный (Т3, массовая концентрация) в сыворотке или плазме", "https://loinc.org/3051-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e17"), new("2160-0", "Креатинин (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2160-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e18"), new("3094-0", "Азот мочевины (массовая концентрация) в сыворотке или плазме", "https://loinc.org/3094-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e19"), new("3084-1", "Мочевая кислота (массовая концентрация) в сыворотке или плазме", "https://loinc.org/3084-1/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1a"), new("1920-8", "Аспартатаминотрансфераза (АСТ, ферментативная активность) в сыворотке или плазме", "https://loinc.org/1920-8/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1b"), new("1742-6", "Аланинаминотрансфераза (АЛТ, ферментативная активность) в сыворотке или плазме", "https://loinc.org/1742-6/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1c"), new("1975-2", "Билирубин общий (массовая концентрация) в сыворотке или плазме", "https://loinc.org/1975-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1d"), new("1968-7", "Билирубин прямой (массовая концентрация) в сыворотке или плазме", "https://loinc.org/1968-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1e"), new("6768-6", "Щелочная фосфатаза (ферментативная активность) в сыворотке или плазме", "https://loinc.org/6768-6/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e1f"), new("2324-2", "Гамма-глутамилтрансфераза (ГГТ, ферментативная активность) в сыворотке или плазме", "https://loinc.org/2324-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e20"), new("1751-7", "Альбумин (массовая концентрация) в сыворотке или плазме", "https://loinc.org/1751-7/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e21"), new("2885-2", "Белок общий (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2885-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e22"), new("2093-3", "Холестерин общий (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2093-3/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e23"), new("2085-9", "Холестерин ЛПВП (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2085-9/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e24"), new("2571-8", "Триглицериды (массовая концентрация) в сыворотке или плазме", "https://loinc.org/2571-8/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e25"), new("2951-2", "Натрий (молярная концентрация) в сыворотке или плазме", "https://loinc.org/2951-2/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e26"), new("2823-3", "Калий (молярная концентрация) в сыворотке или плазме", "https://loinc.org/2823-3/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e27"), new("2075-0", "Хлориды (молярная концентрация) в сыворотке или плазме", "https://loinc.org/2075-0/")),
        new(Guid.Parse("ad292608-5e95-429b-8389-14dd981c1e28"), new("17861-6", "Кальций (массовая концентрация) в сыворотке или плазме", "https://loinc.org/17861-6/"))
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
