using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.ServiceAccounts;

internal sealed class UnavailableCurrentServiceAccount : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() => throw new ServiceAccountAccessDeniedException();
}
