using Microsoft.AspNetCore.Antiforgery;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Contracts.ServiceAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace HospitalInformationSystem.Api.Middleware;

public sealed class ApiAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, AuthenticationAuditService audit)
    {
        if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
             HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) &&
            !await IsServiceObservationCreateAsync(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                if (HttpMethods.IsPost(context.Request.Method) && AuthenticationAuditService.IsLoginPath(context.Request.Path))
                    await audit.LoginCsrfRejectedAsync(context.RequestAborted);
                await Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Не удалось проверить безопасность запроса.",
                    detail: "Обновите страницу и повторите действие.").ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static async Task<bool> IsServiceObservationCreateAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !(context.Request.Path.Equals("/fhir/Observation", StringComparison.OrdinalIgnoreCase) ||
              context.Request.Path.Equals("/fhir/Observation/", StringComparison.OrdinalIgnoreCase)) ||
            context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data =>
                data.Policy == ServiceAuthenticationDefaults.Policies.ObservationCreate &&
                data.AuthenticationSchemes == ServiceAuthenticationDefaults.Scheme) != true)
            return false;
        var authentication = await context.AuthenticateAsync(ServiceAuthenticationDefaults.Scheme);
        return authentication.Succeeded && authentication.Ticket?.AuthenticationScheme == ServiceAuthenticationDefaults.Scheme &&
               ServiceAccountPrincipal.TryGetIdentity(authentication.Principal) is not null;
    }
}
