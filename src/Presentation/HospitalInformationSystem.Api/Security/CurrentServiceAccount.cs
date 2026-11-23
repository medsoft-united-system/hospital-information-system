using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Api.Security;

public sealed class CurrentServiceAccount(IHttpContextAccessor contexts) : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() =>
        ServiceAccountPrincipal.TryGetIdentity(contexts.HttpContext?.User)
        ?? throw new ServiceAccountAccessDeniedException();
}
