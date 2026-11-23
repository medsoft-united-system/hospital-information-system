using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.ServiceAccounts;

internal sealed class UnavailableCurrentServiceAccount : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() => throw new ServiceAccountAccessDeniedException();
}
