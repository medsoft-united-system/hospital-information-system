using HospitalInformationSystem.Domain.ServiceAccounts;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

internal sealed class ServiceAccountReadRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ServiceAccountRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public uint Version { get; set; }
}
