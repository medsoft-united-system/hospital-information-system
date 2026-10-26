using System.Security.Claims;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Contracts.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HospitalInformationSystem.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<HospitalUser> userManager,
    SignInManager<HospitalUser> signInManager,
    IAccountService accounts,
    FailedLoginPasswordVerifier failedLoginVerifier,
    AuthenticationAuditService audit,
    TokenSessionService tokenSessions,
    RefreshCookieService refreshCookies,
    IAdministratorSetupService administratorSetup) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public ActionResult<AntiforgeryTokenDto> GetAntiforgeryToken(IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenDto(tokens.RequestToken!));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<ActionResult<AuthenticationTokensDto>> LoginAsync(LoginRequest request,
        CancellationToken cancellationToken)
    {
        var previousRefresh = refreshCookies.Read(HttpContext);
        if (previousRefresh is not null)
        {
            await tokenSessions.RevokeFamilyAsync(previousRefresh, cancellationToken);
            refreshCookies.Clear(HttpContext);
        }
        var userName = request.UserName?.Trim();
        if (!AccountUserNameRules.HasValidLength(userName) ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 128 ||
            request.Portal is { } portal && !Enum.IsDefined(portal))
            return await InvalidCredentialsAsync(userName, cancellationToken);

        var user = await userManager.FindByNameAsync(userName!);
        if (user is null || !user.IsActive)
        {
            failedLoginVerifier.Verify(request.Password);
            return await InvalidCredentialsAsync(userName, cancellationToken);
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut) failedLoginVerifier.Verify(request.Password);
            return await InvalidCredentialsAsync(userName, cancellationToken);
        }

        var session = await accounts.GetSessionAsync(user.Id, cancellationToken);
        if (session is null || !MatchesPortal(session.Role, request.Portal))
            return await InvalidCredentialsAsync(userName, cancellationToken);

        if (session.Role == ApplicationRoles.Administrator &&
            !await administratorSetup.VerifyAdministratorCodeAsync(user.Id, request.TotpCode, cancellationToken))
            return await InvalidCredentialsAsync(userName, cancellationToken);
        if (session.Role == ApplicationRoles.MedicalWorker && !string.IsNullOrEmpty(request.TotpCode))
            return await InvalidCredentialsAsync(userName, cancellationToken);

        var issued = await tokenSessions.IssueAsync(user.Id, user.SecurityStamp!, cancellationToken);
        if (issued is null) return await InvalidCredentialsAsync(userName, cancellationToken);
        refreshCookies.Write(HttpContext, issued);
        return Ok(issued.Tokens);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<ActionResult<AuthenticationTokensDto>> RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = refreshCookies.Read(HttpContext);
        var result = refresh is null ? null : await tokenSessions.RotateAsync(refresh, cancellationToken);
        if (result is null)
        {
            refreshCookies.Clear(HttpContext);
            return Unauthorized();
        }
        refreshCookies.Write(HttpContext, result);
        return Ok(result.Tokens);
    }

    [HttpGet("session")]
    [Authorize]
    public async Task<ActionResult<SessionDto>> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(SecurityTokenClaimTypes.Subject), out var id))
            return Unauthorized();
        var session = await accounts.GetSessionAsync(id, cancellationToken);
        if (session is not null)
            return Ok(session);
        return Unauthorized();
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        var refresh = refreshCookies.Read(HttpContext);
        if (User.Identity?.IsAuthenticated == true)
            await tokenSessions.RevokeFamilyAsync(User, cancellationToken);
        else if (refresh is not null)
            await tokenSessions.RevokeFamilyAsync(refresh, cancellationToken);
        refreshCookies.Clear(HttpContext);
        return NoContent();
    }

    private async Task<ObjectResult> InvalidCredentialsAsync(string? attemptedUserName,
        CancellationToken cancellationToken)
    {
        await audit.LoginFailedAsync(attemptedUserName, cancellationToken);
        return InvalidCredentials();
    }

    private ObjectResult InvalidCredentials() => Problem(statusCode: StatusCodes.Status401Unauthorized,
        title: "Не удалось войти.", detail: "Проверьте имя пользователя и пароль или повторите попытку позднее.");

    private static bool MatchesPortal(string role, LoginPortal? portal) => portal switch
    {
        LoginPortal.Administrator => role == ApplicationRoles.Administrator,
        LoginPortal.MedicalWorker => role == ApplicationRoles.MedicalWorker,
        _ => true
    };
}
