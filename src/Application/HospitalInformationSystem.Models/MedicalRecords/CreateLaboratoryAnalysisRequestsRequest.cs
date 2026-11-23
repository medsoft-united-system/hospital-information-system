namespace HospitalInformationSystem.Models.MedicalRecords;

public sealed record CreateLaboratoryAnalysisRequestsRequest(
    IReadOnlyList<Guid> LaboratoryTestDefinitionIds,
    string? Comment,
    IReadOnlyDictionary<Guid, uint>? ExpectedVersions = null);
