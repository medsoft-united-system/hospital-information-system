using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using System.Net;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.Web.ApiClients;

public sealed class SessionApiClient(HttpClient http, SessionState session, AntiforgeryTokenStore tokens,
    AuthenticationOperationCoordinator operations)
{
    public async Task LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        using var operation = await operations.EnterAsync(cancellationToken);
        tokens.Reset();
        try
        {
            var current = await http.SendJsonAsync<AuthenticationTokensDto>(HttpMethod.Post, "api/auth/login", request, null,
                cancellationToken);
            tokens.Reset();
            session.SetTokens(current);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Reset();
            session.SetSession(null);
            throw;
        }
    }

    public async Task<AdministratorLoginChallengeDto> BeginAdministratorLoginAsync(
        BeginAdministratorLoginRequest request, CancellationToken cancellationToken)
    {
        using var operation = await operations.EnterAsync(cancellationToken);
        tokens.Reset();
        try
        {
            var challenge = await http.SendJsonAsync<AdministratorLoginChallengeDto>(HttpMethod.Post,
                "api/auth/admin/login", request, null, cancellationToken);
            tokens.Reset();
            session.SetSession(null);
            return challenge;
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Reset();
            session.SetSession(null);
            throw;
        }
    }

    public async Task ConfirmAdministratorLoginAsync(ConfirmAdministratorLoginRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = await operations.EnterAsync(cancellationToken);
        tokens.Reset();
        var current = await http.SendJsonAsync<AuthenticationTokensDto>(HttpMethod.Post,
            "api/auth/admin/confirm", request, null, cancellationToken);
        tokens.Reset();
        session.SetTokens(current);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        using var operation = await operations.EnterAsync(cancellationToken);
        tokens.Reset();
        await http.SendJsonAsync(HttpMethod.Post, "api/auth/logout", null, null, cancellationToken);
        tokens.Reset();
        session.SetSession(null);
    }
}
