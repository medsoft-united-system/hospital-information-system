using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Web.Authentication;

public sealed class SessionState
{
    public SessionDto? Current { get; private set; }
    public string? AccessToken { get; private set; }
    public bool Initialized { get; private set; }
    public long Version { get; private set; }
    public event Action? Changed;

    public void SetSession(SessionDto? session)
    {
        if (session is null || Current?.Id != session.Id || Current.Role != session.Role)
            AccessToken = null;
        Current = session;
        Initialized = true;
        Version++;
        Changed?.Invoke();
    }

    public void SetTokens(AuthenticationTokensDto tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens.AccessToken);
        AccessToken = tokens.AccessToken;
        var current = tokens.Session;
        Current = new SessionDto(current.Id, current.UserName, current.AccountRole,
            current.MedicalWorkerId, current.MedicalWorkerRole);
        Initialized = true;
        Version++;
        Changed?.Invoke();
    }
}
