using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HospitalInformationSystem.IntegrationTests;

internal sealed class JwtBrowserTestHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();
    private string? _accessToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var cookies = _cookies.GetCookieHeader(uri);
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (request.Headers.Authorization is null && _accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) _cookies.SetCookies(uri, value);
        if (response.IsSuccessStatusCode && uri.AbsolutePath is "/api/auth/login" or "/api/auth/refresh")
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _accessToken = json.RootElement.GetProperty("accessToken").GetString();
        }
        if (uri.AbsolutePath == "/api/auth/logout" && response.IsSuccessStatusCode) _accessToken = null;
        return response;
    }
}
