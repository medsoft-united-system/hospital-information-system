using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.MedicalRecords;

namespace HospitalInformationSystem.Api.Fhir;

public sealed record FhirSearchParameters(
    string ResourceType,
    LaboratoryAnalysisRequestStatus? Status,
    Guid? BasedOn,
    int Count,
    FhirPagePosition? After,
    string? Cursor);
