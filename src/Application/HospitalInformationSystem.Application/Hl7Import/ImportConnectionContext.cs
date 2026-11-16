using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Application.Hl7Import;

public sealed record ImportConnectionContext(
    ServiceAccountIdentity ServiceAccount,
    ImportClientCertificate ClientCertificate,
    DateTimeOffset ReceivedAtUtc,
    string? RemoteAddress);
