using HospitalInformationSystem.Application.Models.Fhir;
using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Api.Fhir;

public sealed record FhirSearchParameters(
    string ResourceType,
    LaboratoryAnalysisRequestStatus? Status,
    Guid? BasedOn,
    int Count,
    FhirPagePosition? After,
    string? Cursor);
