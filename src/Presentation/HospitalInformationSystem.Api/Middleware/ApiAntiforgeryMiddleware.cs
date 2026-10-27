using Microsoft.AspNetCore.Antiforgery;
using HospitalInformationSystem.Api.Security;

namespace HospitalInformationSystem.Api.Middleware;

public sealed class ApiAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, AuthenticationAuditService audit)
    {
        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                if (HttpMethods.IsPost(context.Request.Method) &&
                    string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/auth/login",
                        StringComparison.OrdinalIgnoreCase))
                    await audit.LoginCsrfRejectedAsync(context.RequestAborted);
                await Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Не удалось проверить безопасность запроса.",
                    detail: "Обновите страницу и повторите действие.").ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
