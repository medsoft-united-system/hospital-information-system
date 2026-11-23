namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public static class ServiceAuthenticationDefaults
{
    public const string Scheme = "ServiceBearer";
    public const string Audience = "MedSoft.Lis";
    public const string Issuer = "MedSoft.Api";
    public const string AccountKind = "service";
    public const string Role = "LIS";
    public const string FhirVersion = "5.0.0";

    public static class Policies
    {
        public const string FhirRead = "LisFhirRead";
        public const string ObservationCreate = "LisObservationCreate";
        public const string ConnectionProbe = "LisConnectionProbe";
    }

    public static class Claims
    {
        public const string Subject = "sub";
        public const string AccountKind = "account_kind";
        public const string Role = "role";
        public const string SessionId = "sid";
        public const string AuthenticationVersion = "auth_ver";
        public const string TokenId = "jti";
        public const string IssuedAt = "iat";
        public const string NotBefore = "nbf";
        public const string ExpiresAt = "exp";
    }
}
