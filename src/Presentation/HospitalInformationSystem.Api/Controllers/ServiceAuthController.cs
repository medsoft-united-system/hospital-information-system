using System.Security.Claims;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.Models.ServiceAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ServiceAuthController(IServiceTokenSessionService sessions, ServiceCredentialReader reader) : ControllerBase
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
    [Authorize(Policy = ServiceAuthenticationDefaults.Policies.ConnectionProbe)]
    public ActionResult TestConnection()
    {
        if (Request.QueryString.HasValue) throw ServiceAuthProtocol.Error(400);
        var service = ServiceAccountPrincipal.TryGetIdentity(User);
        if (service is not null)
            return service.Role == ServiceAccountRole.Lis
                ? Ok(new ServiceConnectionStatusDto("connected", service.Id,
                    ServiceAccountRoles.Lis, ServiceAuthenticationDefaults.FhirVersion))
                : Ok(new Hl7ServiceConnectionStatusDto("connected", service.Id,
                    ServiceAccountRoles.Hl7Import, Hl7ImportProtocol.Version));

        var subjects = User.FindAll(SecurityTokenClaimTypes.Subject).Take(2).ToArray();
        var roles = User.FindAll(SecurityTokenClaimTypes.AccountRole).Take(2).ToArray();
        if (subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out var accountId) ||
            accountId == Guid.Empty || roles.Length != 1 ||
            roles[0].Value is not (ApplicationRoles.Administrator or ApplicationRoles.MedicalWorker))
            throw ServiceAuthProtocol.Error(403);
        return Ok(new HumanConnectionStatusDto("connected", accountId, roles[0].Value, "human"));
    }
}
