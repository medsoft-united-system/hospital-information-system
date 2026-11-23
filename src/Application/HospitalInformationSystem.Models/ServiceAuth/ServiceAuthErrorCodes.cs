namespace HospitalInformationSystem.Models.ServiceAuth;

public static class ServiceAuthErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidRefresh = "invalid_refresh";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RequestTooLarge = "request_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
}
