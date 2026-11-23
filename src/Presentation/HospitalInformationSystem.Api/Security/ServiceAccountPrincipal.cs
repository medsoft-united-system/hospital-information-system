using System.Security.Claims;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Api.Security;

public static class ServiceAccountPrincipal
{
    public static ServiceAccountIdentity? TryGetIdentity(ClaimsPrincipal? principal) =>
        ServiceAccountIdentityReader.TryRead(principal);
}
