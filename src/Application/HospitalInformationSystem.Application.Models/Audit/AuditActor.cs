using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Models.Audit;

public sealed record AuditActor(
    AuditActorKind Kind,
    Guid? UserId,
    string DisplayName,
    string? RequestId,
    string? Component,
    Guid? ServiceAccountId = null);
