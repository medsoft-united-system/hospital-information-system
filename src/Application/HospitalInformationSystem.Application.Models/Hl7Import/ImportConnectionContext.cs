using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Models.Hl7Import;

public sealed record ImportConnectionContext(
    ServiceAccountIdentity ServiceAccount,
    ImportClientCertificate ClientCertificate,
    DateTimeOffset ReceivedAtUtc,
    string? RemoteAddress);
