namespace HospitalInformationSystem.Domain.ServiceAccounts;

public sealed class ServiceAccount
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public ServiceAccountRole Role { get; init; } = ServiceAccountRole.Lis;
    public bool IsActive { get; set; } = true;
    public required byte[] ApiKeyHash { get; set; }
    public long AuthenticationVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
