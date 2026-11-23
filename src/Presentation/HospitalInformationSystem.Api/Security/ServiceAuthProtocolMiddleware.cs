using System.Text.Json;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.ServiceAuth;

namespace HospitalInformationSystem.Api.Security;

public sealed class ServiceAuthProtocolMiddleware(RequestDelegate next, ILogger<ServiceAuthProtocolMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ServiceAuthProtocol.IsTechnicalPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        ServiceAuthHttpException? error = null;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return;
        }
        catch (Exception exception)
        {
            error = exception switch
            {
                ServiceAuthHttpException service => service,
                BadHttpRequestException badRequest => ServiceAuthProtocol.Error(badRequest.StatusCode),
                DomainValidationException => ServiceAuthProtocol.Error(400),
                ServiceAccountAccessDeniedException => ServiceAuthProtocol.Error(403),
                _ => ServiceAuthProtocol.Error(500)
            };
            if (error.StatusCode == 500)
                logger.LogError("Unexpected {ExceptionType} in service authentication; trace {TraceId}",
                    exception.GetType().FullName, context.TraceIdentifier);
        }
        finally
        {
            context.Response.Body = original;
        }

        context.Response.Headers.CacheControl = "no-store";
        if (error is not null || context.Response.StatusCode >= 400)
        {
            error ??= ServiceAuthProtocol.Error(context.Response.StatusCode);
            context.Response.ContentLength = null;
            context.Response.StatusCode = error.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (error.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new ServiceAuthErrorDto(error.Code, error.Message, context.TraceIdentifier),
                cancellationToken: context.RequestAborted);
            return;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }
}
