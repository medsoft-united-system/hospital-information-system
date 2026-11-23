using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Models.Fhir;

public sealed record FhirServiceRequestReadModel(Guid Id, Guid PatientId, Guid RequestedByMedicalWorkerId,
    string LoincCode, string DisplayName, string? Comment, DateTimeOffset AuthoredAtUtc,
    LaboratoryAnalysisRequestStatus Status);

public sealed record FhirObservationReadModel(Guid Id, Guid LaboratoryAnalysisRequestId, Guid PatientId,
    string LoincCode, string DisplayName, string Value, DateTimeOffset IssuedAtUtc);

public sealed record FhirPagePosition(DateTimeOffset AtUtc, Guid Id);

public sealed record FhirReadPage<T>(IReadOnlyList<T> Items, FhirPagePosition? Next);
