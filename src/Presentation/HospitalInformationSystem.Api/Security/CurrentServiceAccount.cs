using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Api.Security;

public sealed class CurrentServiceAccount(IHttpContextAccessor contexts) : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() =>
        ServiceAccountPrincipal.TryGetIdentity(contexts.HttpContext?.User)
        ?? throw new ServiceAccountAccessDeniedException();
}
