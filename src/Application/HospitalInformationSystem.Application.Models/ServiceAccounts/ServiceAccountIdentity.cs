using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Models.ServiceAccounts;

public sealed record ServiceAccountIdentity(Guid Id, string Name, long AuthenticationVersion,
    ServiceAccountRole Role = ServiceAccountRole.Lis);
