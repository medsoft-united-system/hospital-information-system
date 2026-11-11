using HospitalInformationSystem.Contracts.ServiceAuth;

namespace HospitalInformationSystem.Api.Security;

public static class ServiceAuthProtocol
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string RateLimitPolicy = "ServiceCredentials";

    public static bool IsTechnicalPath(PathString path) =>
        IsPath(path, "/api/service-auth/token") || IsPath(path, "/api/service-auth/refresh") ||
        IsPath(path, "/api/testConnection");

    public static bool IsCredentialRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) &&
        (IsPath(context.Request.Path, "/api/service-auth/token") ||
         IsPath(context.Request.Path, "/api/service-auth/refresh")) &&
        context.GetEndpoint()?.Metadata.GetMetadata<ServiceCredentialEndpointAttribute>() is not null;

    public static bool IsPath(PathString path, string expected) =>
        path.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(expected + "/", StringComparison.OrdinalIgnoreCase);

    public static ServiceAuthHttpException Error(int status) => status switch
    {
        400 or 405 => new(status, ServiceAuthErrorCodes.InvalidRequest, "Некорректный сервисный запрос."),
        401 => new(status, ServiceAuthErrorCodes.Unauthorized, "Требуется действующая сервисная авторизация."),
        403 => new(status, ServiceAuthErrorCodes.Forbidden, "Сервисной учётной записи недостаточно прав."),
        413 => new(status, ServiceAuthErrorCodes.RequestTooLarge, "Размер запроса превышает допустимый."),
        415 => new(status, ServiceAuthErrorCodes.UnsupportedMediaType, "Тип содержимого запроса не поддерживается."),
        429 => new(status, ServiceAuthErrorCodes.RateLimited, "Слишком много запросов; повторите действие позднее."),
        _ => new(status, ServiceAuthErrorCodes.InternalError, "Не удалось выполнить сервисный запрос.")
    };
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ServiceCredentialEndpointAttribute : Attribute;

public sealed class ServiceAuthHttpException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
