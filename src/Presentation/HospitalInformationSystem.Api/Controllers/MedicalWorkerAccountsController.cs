using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiSecurityPolicies.Administrator)]
[Route("api/medical-workers/{medicalWorkerId:guid}/account")]
public sealed class MedicalWorkerAccountsController(IAccountService accounts) : ControllerBase
{
    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<CreateWorkerAccountResultDto>> CreateAsync(Guid medicalWorkerId,
        CreateWorkerAccountRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created,
            await accounts.CreateForWorkerAsync(medicalWorkerId, request, cancellationToken));
}
