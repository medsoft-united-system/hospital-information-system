using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiSecurityPolicies.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/service-accounts")]
public sealed class ServiceAccountsController(IServiceAccountService accounts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ServiceAccountDto>>> ListAsync(CancellationToken cancellationToken) =>
        Ok(await accounts.ListAsync(cancellationToken));

    [HttpGet("{id}")]
    public async Task<ActionResult<ServiceAccountDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await accounts.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<ServiceAccountKeyResultDto>> CreateAsync(CreateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        var result = await accounts.CreateAsync(request, cancellationToken);
        return Created($"/api/service-accounts/{result.Account.Id:D}", result);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ServiceAccountDto>> UpdateAsync(Guid id, UpdateServiceAccountRequest request,
        CancellationToken cancellationToken) => Ok(await accounts.UpdateAsync(id, request, cancellationToken));

    [HttpPut("{id}/active")]
    public async Task<ActionResult<ServiceAccountActivityResultDto>> SetActiveAsync(Guid id,
        SetServiceAccountActiveRequest request, CancellationToken cancellationToken) =>
        Ok(await accounts.SetActiveAsync(id, request, cancellationToken));

    [HttpPost("{id}/rotate-key")]
    public async Task<ActionResult<ServiceAccountKeyResultDto>> RotateKeyAsync(Guid id,
        RotateServiceAccountKeyRequest request, CancellationToken cancellationToken) =>
        Ok(await accounts.RotateKeyAsync(id, request, cancellationToken));
}
