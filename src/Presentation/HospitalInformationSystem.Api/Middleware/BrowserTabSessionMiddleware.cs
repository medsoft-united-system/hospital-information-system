using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HospitalInformationSystem.Models.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace HospitalInformationSystem.Api.Middleware;

public sealed class BrowserTabSessionMiddleware(RequestDelegate next, IOptions<AntiforgeryOptions> antiforgeryOptions)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(BrowserTabSession.HeaderName, out var values) &&
            (values.Count != 1 || !BrowserTabSession.IsValid(values[0])))
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный идентификатор вкладки браузера.",
                detail: "Обновите страницу и повторите действие.").ExecuteAsync(context);
            return;
        }
        if (values.Count == 1)
            ScopeAntiforgeryCookie(context);
        await next(context);
    }

    private void ScopeAntiforgeryCookie(HttpContext context)
    {
        var baseName = antiforgeryOptions.Value.Cookie.Name
            ?? throw new InvalidOperationException("Не настроено имя cookie защиты CSRF.");
        var scopedName = Security.BrowserTabCookieNames.ForRequest(context, baseName);
        var original = context.Request.Cookies;
        var cookies = original.Where(item => !string.Equals(item.Key, baseName, StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (original.TryGetValue(scopedName, out var value)) cookies[baseName] = value;
        context.Request.Cookies = new ScopedCookies(cookies);
        context.Response.OnStarting(() =>
        {
            var prefix = baseName + "=";
            var headers = context.Response.Headers.SetCookie;
            if (headers.Count > 0)
                context.Response.Headers.SetCookie = new StringValues(headers
                    .Select(header => header?.StartsWith(prefix, StringComparison.Ordinal) == true
                        ? scopedName + header[baseName.Length..] : header).ToArray());
            return Task.CompletedTask;
        });
    }

    private sealed class ScopedCookies(Dictionary<string, string> values) : IRequestCookieCollection
    {
        public string? this[string key] => values.GetValueOrDefault(key);
        public int Count => values.Count;
        public ICollection<string> Keys => values.Keys;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
