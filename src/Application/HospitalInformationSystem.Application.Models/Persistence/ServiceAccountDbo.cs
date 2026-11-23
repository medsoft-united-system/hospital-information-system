using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class ServiceAccountDbo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public ServiceAccountRole Role { get; set; } = ServiceAccountRole.Lis;
    public bool IsActive { get; set; } = true;
    public byte[] ApiKeyHash { get; set; } = [];
    public long AuthenticationVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
