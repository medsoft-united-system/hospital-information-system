using HospitalInformationSystem.Contracts.Auth;

namespace HospitalInformationSystem.Api.Security;

internal static class BrowserTabCookieNames
{
    public static string ForRequest(HttpContext context, string baseName)
    {
        if (!context.Request.Headers.TryGetValue(BrowserTabSession.HeaderName, out var values))
            return baseName;
        if (values.Count != 1 || !BrowserTabSession.IsValid(values[0]))
            throw new InvalidOperationException("Некорректный идентификатор вкладки браузера.");
        return baseName + "." + values[0];
    }
}
