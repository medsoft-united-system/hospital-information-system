using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiSecurityPolicies.Administrator)]
[Route("api/audit")]
public sealed class AuditController(IAuditQueryService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditEventDto>>> GetAsync([FromQuery] AuditQuery query,
        CancellationToken cancellationToken) => Ok(await audit.ListAsync(query, cancellationToken));
}
