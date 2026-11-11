namespace HospitalInformationSystem.Api.Fhir;

public sealed record FhirObservationInput(Guid RequestId, Guid PatientId, string LoincCode, string Value,
    DateTimeOffset IssuedAtUtc);
