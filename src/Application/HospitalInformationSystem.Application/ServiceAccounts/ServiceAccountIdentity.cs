using HospitalInformationSystem.Domain.ServiceAccounts;

namespace HospitalInformationSystem.Application.ServiceAccounts;

public sealed record ServiceAccountIdentity(Guid Id, string Name, long AuthenticationVersion,
    ServiceAccountRole Role = ServiceAccountRole.Lis);
