namespace HospitalInformationSystem.Models.Auth;

public sealed record LoginRequest(string UserName, string Password, string? TotpCode = null,
    LoginPortal? Portal = null);
