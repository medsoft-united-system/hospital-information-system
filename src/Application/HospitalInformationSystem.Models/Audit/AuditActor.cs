using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Models.Audit;

public sealed record AuditActor(
    AuditActorKind Kind,
    Guid? UserId,
    string DisplayName,
    string? RequestId,
    string? Component,
    Guid? ServiceAccountId = null);
