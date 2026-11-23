using HospitalInformationSystem.Application.Models.Patients;

namespace HospitalInformationSystem.Application.Models.Hl7Import;

public sealed record ImportedPatientInput
{
    public required string SourceSystem { get; init; }
    public required string ExternalPatientId { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? MiddleName { get; init; }
    public required DateOnly BirthDate { get; init; }
    public required AdministrativeSex Sex { get; init; }
    public required IReadOnlyList<ImportedDiagnosisInput> Diagnoses { get; init; }
}
