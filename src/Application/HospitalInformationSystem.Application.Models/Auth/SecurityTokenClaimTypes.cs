namespace HospitalInformationSystem.Application.Models.Auth;

public static class SecurityTokenClaimTypes
{
    public const string Subject = "sub";
    public const string UserName = "name";
    public const string AccountRole = "account_role";
    public const string MedicalRole = "medical_role";
    public const string SessionFamily = "sid";
    public const string SessionId = "jti";
    public const string AuthenticationVersion = "auth_version";
}
