using HospitalInformationSystem.Application.LaboratoryTests.Services;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.LaboratoryTests;

namespace HospitalInformationSystem.Application.LaboratoryTests.Factories;

public static class LaboratoryTestDefinitionFactory
{
    public static LaboratoryTestDefinition Create(string loincCode, string displayName, DateTimeOffset now)
    {
        var definition = new LaboratoryTestDefinition
        {
            Id = Guid.NewGuid(),
            LoincCode = string.Empty,
            DisplayName = string.Empty,
            IsActive = true,
            CreatedAtUtc = now.ToUniversalTime()
        };
        LaboratoryTestDefinitionService.Update(definition, loincCode, displayName, now);
        return definition;
    }
}
