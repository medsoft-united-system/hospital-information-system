using HospitalInformationSystem.Api.Security;
using Microsoft.AspNetCore.Authorization;

namespace HospitalInformationSystem.IntegrationTests;

internal sealed class BusinessMedicalRoleHandler : AuthorizationHandler<MedicalRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        MedicalRoleRequirement requirement)
    {
        if (context.User.Identity?.AuthenticationType == BusinessTestAuthentication.Scheme)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
