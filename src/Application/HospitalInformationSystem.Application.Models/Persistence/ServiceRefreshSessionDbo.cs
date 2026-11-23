namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class ServiceRefreshSessionDbo
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public long AuthenticationVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokeReason { get; set; }
}
