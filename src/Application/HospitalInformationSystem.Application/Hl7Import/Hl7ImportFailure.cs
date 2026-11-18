using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed record Hl7ImportFailure(
    string MessageControlId,
    string Outcome,
    ImportClientCertificate ClientCertificate,
    ServiceAccountIdentity? ServiceAccount = null,
    string? SourceSystem = null,
    string? ExternalPatientId = null);
