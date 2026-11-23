using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Models.Hl7Import;

public sealed record Hl7ImportFailure(
    string MessageControlId,
    string Outcome,
    ImportClientCertificate ClientCertificate,
    ServiceAccountIdentity? ServiceAccount = null,
    string? SourceSystem = null,
    string? ExternalPatientId = null);
