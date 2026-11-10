using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ServiceAuthController(ServiceTokenSessionService sessions, ServiceCredentialReader reader,
    ICurrentServiceAccount current) : ControllerBase
{
    [HttpPost("api/service-auth/token")]
    [AllowAnonymous]
    [ServiceCredentialEndpoint]
    [EnableRateLimiting(ServiceAuthProtocol.RateLimitPolicy)]
    [RequestSizeLimit(ServiceAuthProtocol.MaxBodyBytes)]
    public async Task<ActionResult<ServiceTokenPairDto>> TokenAsync(CancellationToken cancellationToken)
    {
        var key = await reader.ReadAsync(Request, "apiKey", cancellationToken);
        var result = await sessions.AuthenticateAsync(key, cancellationToken);
        if (result is null)
            throw new ServiceAuthHttpException(401, ServiceAuthErrorCodes.InvalidCredentials,
                "Не удалось подтвердить сервисные учётные данные.");
        return Ok(result);
    }

    [HttpPost("api/service-auth/refresh")]
    [AllowAnonymous]
    [ServiceCredentialEndpoint]
    [EnableRateLimiting(ServiceAuthProtocol.RateLimitPolicy)]
    [RequestSizeLimit(ServiceAuthProtocol.MaxBodyBytes)]
    public async Task<ActionResult<ServiceTokenPairDto>> RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = await reader.ReadAsync(Request, "refreshToken", cancellationToken);
        var result = await sessions.RefreshAsync(refresh, cancellationToken);
        if (result is null)
            throw new ServiceAuthHttpException(401, ServiceAuthErrorCodes.InvalidRefresh,
                "Не удалось обновить сервисную сессию.");
        return Ok(result);
    }

    [HttpGet("api/testConnection")]
    [Authorize(AuthenticationSchemes = ServiceAuthenticationDefaults.Scheme,
        Policy = ServiceAuthenticationDefaults.Policies.ConnectionProbe)]
    public ActionResult<ServiceConnectionStatusDto> TestConnection()
    {
        if (Request.QueryString.HasValue) throw ServiceAuthProtocol.Error(400);
        var identity = current.RequireIdentity();
        return Ok(new ServiceConnectionStatusDto("connected", identity.Id,
            ServiceAuthenticationDefaults.Role, ServiceAuthenticationDefaults.FhirVersion));
    }
}
