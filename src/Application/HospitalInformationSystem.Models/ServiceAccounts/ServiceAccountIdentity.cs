using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Models.ServiceAccounts;

public sealed record ServiceAccountIdentity(Guid Id, string Name, long AuthenticationVersion,
    ServiceAccountRole Role = ServiceAccountRole.Lis);
