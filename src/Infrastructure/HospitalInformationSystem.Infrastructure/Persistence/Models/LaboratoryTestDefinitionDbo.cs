namespace HospitalInformationSystem.Infrastructure.Persistence.Models;

public sealed class LaboratoryTestDefinitionDbo
{
    public Guid Id { get; set; }
    public string LoincCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
