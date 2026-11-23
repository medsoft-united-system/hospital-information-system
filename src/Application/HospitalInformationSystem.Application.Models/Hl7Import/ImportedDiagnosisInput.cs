namespace HospitalInformationSystem.Application.Models.Hl7Import;

public sealed record ImportedDiagnosisInput
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string CodingSystem { get; init; }
    public required DateTimeOffset DiagnosedAt { get; init; }
    public required string DiagnosisType { get; init; }
}
