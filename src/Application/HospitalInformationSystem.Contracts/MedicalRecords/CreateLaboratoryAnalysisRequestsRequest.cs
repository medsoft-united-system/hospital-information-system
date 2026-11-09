namespace HospitalInformationSystem.Contracts.MedicalRecords;

public sealed record CreateLaboratoryAnalysisRequestsRequest(
    IReadOnlyList<Guid> LaboratoryTestDefinitionIds,
    string? Comment);
