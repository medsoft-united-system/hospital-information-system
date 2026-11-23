using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Api.Security;

public sealed class CurrentServiceAccount(IHttpContextAccessor contexts) : ICurrentServiceAccount
{
    public ServiceAccountIdentity RequireIdentity() =>
        ServiceAccountPrincipal.TryGetIdentity(contexts.HttpContext?.User)
        ?? throw new ServiceAccountAccessDeniedException();
}
