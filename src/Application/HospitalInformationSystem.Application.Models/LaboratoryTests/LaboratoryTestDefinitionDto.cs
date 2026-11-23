namespace HospitalInformationSystem.Application.Models.LaboratoryTests;

public sealed record LaboratoryTestDefinitionDto(Guid Id, string LoincCode, string DisplayName, bool IsActive,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, uint Version = 0);
