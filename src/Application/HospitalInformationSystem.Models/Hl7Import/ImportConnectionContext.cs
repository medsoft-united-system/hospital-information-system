using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Models.Hl7Import;

public sealed record ImportConnectionContext(
    ServiceAccountIdentity ServiceAccount,
    ImportClientCertificate ClientCertificate,
    DateTimeOffset ReceivedAtUtc,
    string? RemoteAddress);
