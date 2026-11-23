using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth/setup")]
public sealed class AdministratorSetupController(IAdministratorSetupService setup) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<SetupStatusDto>> GetStatusAsync(CancellationToken cancellationToken) =>
        Ok(await setup.GetStatusAsync(cancellationToken));

    [HttpPost("begin")]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<ActionResult<SetupChallengeDto>> BeginAsync(BeginSetupRequest request,
        CancellationToken cancellationToken) =>
        Ok(await setup.BeginAsync(request, OwnerToken(), cancellationToken));

    [HttpPost("confirm")]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<IActionResult> ConfirmAsync(ConfirmSetupRequest request, CancellationToken cancellationToken)
    {
        await setup.ConfirmAsync(request, OwnerToken(), cancellationToken);
        return NoContent();
    }

    private string OwnerToken() => Request.Headers["X-Setup-Token"] is { Count: 1 } values ? values[0]! : "";
}
