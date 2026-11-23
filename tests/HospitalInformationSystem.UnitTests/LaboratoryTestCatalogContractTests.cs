using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Initializers;
using HospitalInformationSystem.Models.LaboratoryTests;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryTestCatalogContractTests
{
    [Fact]
    public void SharedCatalogFixture_ShouldMatchSeedAndKeepSevenSourcedLoincDefinitions()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab3",
            "laboratory-test-catalog.json"));
        var definitions = JsonSerializer.Deserialize<LaboratoryTestCatalogEntry[]>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        definitions.Should().BeEquivalentTo(LaboratoryTestCatalogInitializer.Definitions);
        definitions.Select(x => x.Code).Should().BeEquivalentTo(
            ["2345-7", "718-7", "7918-6", "5196-1", "13955-0", "5393-4", "101363-0"]);
        definitions.Select(x => x.Code).Should().OnlyHaveUniqueItems();
        foreach (var definition in definitions)
        {
            LaboratoryTestDefinitionRules.IsValidCode(definition.Code).Should().BeTrue();
            LaboratoryTestDefinitionRules.IsValidDisplayName(definition.DisplayName).Should().BeTrue();
            definition.SourceUrl.Should().Be($"https://loinc.org/{definition.Code}/");
        }
    }
}
