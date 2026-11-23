using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiSecurityPolicies.Administrator)]
[Route("api/accounts")]
public sealed class AccountsController(IAccountService accounts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AccountDetailsDto>>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await accounts.ListDetailsAsync(cancellationToken));

    [HttpPost]
    public IActionResult Create() => Problem(
        statusCode: StatusCodes.Status410Gone,
        title: "Используйте создание аккаунта из карточки медработника.",
        detail: "Пароль генерируется сервером; создание дополнительных администраторов через API недоступно.");

    [HttpPost("{id:guid}/reset-password")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ResetAccountPasswordResultDto>> ResetPasswordAsync(Guid id,
        CancellationToken cancellationToken) => Ok(await accounts.ResetPasswordAsync(id, cancellationToken));

    [HttpPut("{id:guid}/activity")]
    public async Task<ActionResult<SetAccountActiveResultDto>> SetActiveAsync(Guid id,
        SetAccountActiveRequest request, CancellationToken cancellationToken) =>
        Ok(await accounts.SetActiveAsync(id, request.IsActive, cancellationToken));
}
