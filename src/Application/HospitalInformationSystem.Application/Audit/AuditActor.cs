using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Application.Audit;

public sealed record AuditActor(
    AuditActorKind Kind,
    Guid? UserId,
    string DisplayName,
    string? RequestId,
    string? Component);
