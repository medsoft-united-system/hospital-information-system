using System.Security.Claims;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;

namespace HospitalInformationSystem.Api.Security;

public sealed class MedicalRoleAuthorizationHandler(
    IAccountService accounts, IHttpContextAccessor accessor) : AuthorizationHandler<MedicalRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        MedicalRoleRequirement requirement)
    {
        if (!context.User.IsInRole(ApplicationRoles.MedicalWorker) ||
            !Guid.TryParse(context.User.FindFirstValue(SecurityTokenClaimTypes.Subject) ??
                           context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return;
        var session = await accounts.GetSessionAsync(id, accessor.HttpContext?.RequestAborted ?? CancellationToken.None);
        if (session?.MedicalWorkerRole is { } role && requirement.AllowedRoles.Contains(role))
            context.Succeed(requirement);
    }
}
