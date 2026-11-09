using FluentValidation;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirProtocolMiddleware(RequestDelegate next, ILogger<FhirProtocolMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, FhirResponseWriter writer)
    {
        if (!context.Request.Path.StartsWithSegments("/fhir"))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        FhirHttpException? error = null;
        try
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Headers.Allow = "GET";
                throw FromStatus(405);
            }
            EnsureJsonAccepted(context.Request);
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return;
        }
        catch (Exception exception)
        {
            error = Translate(exception);
            if (error.StatusCode == 500)
                logger.LogError("Unexpected {ExceptionType} in FHIR request; trace {TraceId}",
                    exception.GetType().FullName, context.TraceIdentifier);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (error is not null || context.Response.StatusCode >= 400)
        {
            error ??= FromStatus(context.Response.StatusCode);
            context.Response.ContentLength = null;
            if (error.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            await writer.WriteAsync(context, FhirResourceMapper.OperationOutcome(error.StatusCode,
                error.MachineCode, error.Message, context.TraceIdentifier), error.StatusCode, context.RequestAborted);
            return;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }

    private static void EnsureJsonAccepted(HttpRequest request)
    {
        if (request.Headers.Accept.Count == 0) return;
        try
        {
            var types = request.GetTypedHeaders().Accept;
            if (types is not null)
            {
                foreach (var media in new[] { "application/fhir+json", "application/json", "application/*", "*/*" })
                {
                    var matching = types.Where(type => string.Equals(type.MediaType.Value, media,
                        StringComparison.OrdinalIgnoreCase) && type.Parameters.All(parameter =>
                        !string.Equals(parameter.Name.Value, "fhirVersion", StringComparison.OrdinalIgnoreCase) ||
                        parameter.Value.Value?.Trim('"') is "5.0" or "5.0.0")).ToArray();
                    if (matching.Length == 0) continue;
                    if (matching.Any(type => type.Quality is null or > 0)) return;
                    break;
                }
            }
        }
        catch (FormatException)
        {
            throw new FhirHttpException(400, "invalid_resource", "Некорректный заголовок Accept.");
        }
        throw new FhirHttpException(406, "unsupported_media_type", "FHIR API поддерживает только JSON.");
    }

    private static FhirHttpException Translate(Exception exception) => exception switch
    {
        FhirHttpException fhir => fhir,
        EntityNotFoundException or DomainEntityNotFoundException => FromStatus(404),
        DomainValidationException or ValidationException =>
            new FhirHttpException(400, "invalid_resource", "Некорректные параметры FHIR-запроса."),
        ServiceAccountAccessDeniedException or ClinicalAccessDeniedException => FromStatus(403),
        BusinessConflictException or BusinessRuleConflictException => FromStatus(409),
        BadHttpRequestException request => FromStatus(request.StatusCode),
        _ => FromStatus(500)
    };

    private static FhirHttpException FromStatus(int status) => status switch
    {
        400 => new(status, "invalid_resource", "Некорректный FHIR-запрос."),
        401 => new(status, "unauthorized", "Требуется действующая сервисная авторизация."),
        403 => new(status, "forbidden", "Сервисной учётной записи недостаточно прав."),
        404 => new(status, "not_found", "FHIR-ресурс или операция не найдены."),
        405 => new(status, "not_supported", "Эта FHIR-операция не поддерживается."),
        406 => new(status, "unsupported_media_type", "FHIR API поддерживает только JSON."),
        409 => new(status, "state_conflict", "Данные изменились; обновите состояние."),
        413 => new(status, "request_too_large", "Размер запроса превышает допустимый."),
        415 => new(status, "unsupported_media_type", "Тип содержимого запроса не поддерживается."),
        429 => new(status, "rate_limited", "Слишком много запросов; повторите действие позднее."),
        _ => new(status, "internal_error", "Не удалось выполнить FHIR-запрос.")
    };
}
