using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.LaboratoryTests.Services;

public static class LaboratoryTestDefinitionService
{
    public static void Update(LaboratoryTestDefinition definition, string loincCode, string displayName,
        DateTimeOffset now)
    {
        if (!LaboratoryTestDefinitionRules.IsValidCode(loincCode))
            throw new DomainValidationException("Код LOINC: от 3 до 32 символов, цифры и контрольная цифра через дефис.");
        if (!LaboratoryTestDefinitionRules.IsValidDisplayName(displayName))
            throw new DomainValidationException("Название анализа: требуется от 3 до 256 символов без нулевого символа.");

        definition.LoincCode = loincCode.Trim();
        definition.DisplayName = displayName.Trim();
        definition.UpdatedAtUtc = now.ToUniversalTime();
    }

    public static void SetActive(LaboratoryTestDefinition definition, bool isActive, DateTimeOffset now)
    {
        if (definition.IsActive == isActive)
            return;
        definition.IsActive = isActive;
        definition.UpdatedAtUtc = now.ToUniversalTime();
    }
}
