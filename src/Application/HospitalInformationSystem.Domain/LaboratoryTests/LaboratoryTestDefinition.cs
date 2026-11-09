namespace HospitalInformationSystem.Domain.LaboratoryTests;

public sealed class LaboratoryTestDefinition
{
    public Guid Id { get; init; }
    public required string LoincCode { get; set; }
    public required string DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
