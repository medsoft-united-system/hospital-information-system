using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;

namespace HospitalInformationSystem.Application.ServiceAccounts;

internal sealed class UnavailableCurrentServiceAccount : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() => throw new ServiceAccountAccessDeniedException();
}
