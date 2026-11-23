using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Web.Development;

public sealed partial class UiMockHttpMessageHandler
{
    private (Guid UserId, DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt)? _administratorLogin;

    private HttpResponseMessage AdministratorAuthentication(HttpRequestMessage request, string operation, string? body)
    {
        if (operation == "login" && request.Method == HttpMethod.Post)
        {
            _administratorLogin = null;
            if (state.SetupRequired) return BadRequest("Сначала завершите настройку.");
            var input = Read<BeginAdministratorLoginRequest>(body);
            var account = state.Accounts.Values.FirstOrDefault(value =>
                string.Equals(value.UserName, input.UserName?.Trim(), StringComparison.OrdinalIgnoreCase));
            state.SignOut();
            if (account is null || account.Role != ApplicationRoles.Administrator || !account.IsActive || account.IsLockedOut
                || state.Passwords.GetValueOrDefault(account.Id) != input.Password)
            {
                state.AddAudit("LoginFailed", "Неуспешная проверка пароля администратора UiMock.");
                return Unauthorized();
            }
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            _administratorLogin = (account.Id, account.UpdatedAt, expiresAt);
            return Json(new AdministratorLoginChallengeDto(account.UserName, expiresAt));
        }

        if (_administratorLogin is not { } attempt || attempt.ExpiresAt <= DateTimeOffset.UtcNow
            || !state.Accounts.TryGetValue(attempt.UserId, out var current) || !current.IsActive || current.IsLockedOut
            || current.Role != ApplicationRoles.Administrator || current.UpdatedAt != attempt.UpdatedAt)
        {
            _administratorLogin = null;
            return Unauthorized();
        }
        if (operation == "challenge" && request.Method == HttpMethod.Get)
            return Json(new AdministratorLoginChallengeDto(current.UserName, attempt.ExpiresAt));
        if (operation == "confirm" && request.Method == HttpMethod.Post)
        {
            if (Read<ConfirmAdministratorLoginRequest>(body).Code != UiMockState.DemonstrationTotpCode)
                return Unauthorized();
            _administratorLogin = null;
            state.SignIn(current);
            state.AddAudit("AdministratorLogin", "Подтверждён двухэтапный вход администратора UiMock.");
            var session = state.CurrentSession!;
            return Json(new AuthenticationTokensDto("ui-mock-access", DateTimeOffset.UtcNow.AddMinutes(5),
                new AuthenticationSessionDto(session.Id, session.UserName, session.Role,
                    session.MedicalWorkerId, session.MedicalWorkerRole)));
        }
        return NotFound();
    }
}
