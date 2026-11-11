namespace HospitalInformationSystem.Application.Audit;

public sealed record AuditMetadata
{
    public string? Action { get; init; }
    public Guid? TargetId { get; init; }
    public string? CorrelationId { get; init; }
    public string? RevokeReason { get; init; }
    public int? RevokedRefreshTokenCount { get; init; }
    public string? AttemptedUserName { get; init; }
    public string? RemoteIp { get; init; }
    public Guid? MedicalWorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ServiceAccountId { get; init; }
    public int? RevokedServiceSessionCount { get; init; }
    public Guid? RecordEntryId { get; init; }
    public string? Outcome { get; init; }
}
