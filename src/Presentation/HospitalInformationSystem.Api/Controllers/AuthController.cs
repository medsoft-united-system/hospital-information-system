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
    IAdministratorSetupService administratorSetup,
    AdministratorLoginChallenges administratorChallenges) : ControllerBase
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
        administratorChallenges.Clear(HttpContext);
        await RevokePreviousRefreshAsync(cancellationToken);
        var userName = request.UserName?.Trim();
        var user = await VerifyPasswordAsync(userName, request.Password, request.Portal, cancellationToken);
        if (user is null) return await InvalidCredentialsAsync(userName, cancellationToken);
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

    [HttpPost("admin/login")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<ActionResult<AdministratorLoginChallengeDto>> BeginAdministratorLoginAsync(
        BeginAdministratorLoginRequest request, CancellationToken cancellationToken)
    {
        administratorChallenges.Clear(HttpContext);
        await RevokePreviousRefreshAsync(cancellationToken);
        var userName = request.UserName?.Trim();
        var user = await VerifyPasswordAsync(userName, request.Password, LoginPortal.Administrator, cancellationToken);
        if (user is null) return await InvalidCredentialsAsync(userName, cancellationToken);
        var challenge = administratorChallenges.Create(HttpContext, user);
        return Ok(new AdministratorLoginChallengeDto(challenge.UserName, challenge.ExpiresAtUtc));
    }

    [HttpGet("admin/challenge")]
    [AllowAnonymous]
    public async Task<ActionResult<AdministratorLoginChallengeDto>> GetAdministratorLoginChallengeAsync(
        CancellationToken cancellationToken)
    {
        var challenge = administratorChallenges.Read(HttpContext);
        if (challenge is null || await FindChallengeAdministratorAsync(challenge, cancellationToken) is null)
        {
            administratorChallenges.Clear(HttpContext);
            return Unauthorized();
        }
        return Ok(new AdministratorLoginChallengeDto(challenge.UserName, challenge.ExpiresAtUtc));
    }

    [HttpPost("admin/confirm")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSecurityPolicies.LoginRateLimit)]
    public async Task<ActionResult<AuthenticationTokensDto>> ConfirmAdministratorLoginAsync(
        ConfirmAdministratorLoginRequest request, CancellationToken cancellationToken)
    {
        var challenge = administratorChallenges.Acquire(HttpContext);
        if (challenge is null) return await InvalidCredentialsAsync(null, cancellationToken);
        try
        {
            var user = await FindChallengeAdministratorAsync(challenge, cancellationToken);
            if (user is null)
            {
                administratorChallenges.Clear(HttpContext);
                return await InvalidCredentialsAsync(challenge.UserName, cancellationToken);
            }
            if (!await administratorSetup.VerifyAdministratorCodeAsync(user.Id, request.Code, cancellationToken))
            {
                if (!administratorChallenges.RejectCode(challenge)) administratorChallenges.Clear(HttpContext);
                return await InvalidCredentialsAsync(challenge.UserName, cancellationToken);
            }
            if (!administratorChallenges.Consume(challenge))
                return await InvalidCredentialsAsync(challenge.UserName, cancellationToken);
            administratorChallenges.Clear(HttpContext);
            var issued = await tokenSessions.IssueAsync(user.Id, challenge.SecurityStamp, cancellationToken);
            if (issued is null) return await InvalidCredentialsAsync(challenge.UserName, cancellationToken);
            refreshCookies.Write(HttpContext, issued);
            return Ok(issued.Tokens);
        }
        finally { administratorChallenges.Release(challenge); }
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
        administratorChallenges.Clear(HttpContext);
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

    private async Task<HospitalUser?> VerifyPasswordAsync(string? userName, string? password, LoginPortal? portal,
        CancellationToken cancellationToken)
    {
        if (!AccountUserNameRules.HasValidLength(userName) || string.IsNullOrEmpty(password) || password.Length > 128 ||
            portal is { } requestedPortal && !Enum.IsDefined(requestedPortal)) return null;
        var user = await userManager.FindByNameAsync(userName!);
        if (user is null || !user.IsActive)
        {
            failedLoginVerifier.Verify(password);
            return null;
        }
        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut) failedLoginVerifier.Verify(password);
            return null;
        }
        var session = await accounts.GetSessionAsync(user.Id, cancellationToken);
        return session is not null && MatchesPortal(session.Role, portal) ? user : null;
    }

    private async Task<HospitalUser?> FindChallengeAdministratorAsync(AdministratorLoginAttempt challenge,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(challenge.UserId.ToString());
        if (user is null || !string.Equals(user.SecurityStamp, challenge.SecurityStamp, StringComparison.Ordinal) ||
            !string.Equals(user.UserName, challenge.UserName, StringComparison.Ordinal)) return null;
        var session = await accounts.GetSessionAsync(user.Id, cancellationToken);
        return session?.Role == ApplicationRoles.Administrator ? user : null;
    }

    private async Task RevokePreviousRefreshAsync(CancellationToken cancellationToken)
    {
        var previousRefresh = refreshCookies.Read(HttpContext);
        if (previousRefresh is null) return;
        await tokenSessions.RevokeFamilyAsync(previousRefresh, cancellationToken);
        refreshCookies.Clear(HttpContext);
    }

    private static bool MatchesPortal(string role, LoginPortal? portal) => portal switch
    {
        LoginPortal.Administrator => role == ApplicationRoles.Administrator,
        LoginPortal.MedicalWorker => role == ApplicationRoles.MedicalWorker,
        _ => true
    };
}
