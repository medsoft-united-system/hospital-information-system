using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;

namespace HospitalInformationSystem.Api.Security;

public sealed class RefreshCookieService(IWebHostEnvironment environment)
{
    private bool Local => environment.IsDevelopment() || environment.IsEnvironment("Testing");
    public string Name => Local ? "MedSoft.Refresh" : "__Host-MedSoft.Refresh";

    public string? Read(HttpContext context) => context.Request.Cookies[BrowserTabCookieNames.ForRequest(context, Name)];

    public void Write(HttpContext context, TokenSessionResult result) =>
        context.Response.Cookies.Append(BrowserTabCookieNames.ForRequest(context, Name), result.RefreshToken,
            Options(result.RefreshTokenExpiresAt));

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(BrowserTabCookieNames.ForRequest(context, Name), Options(null));

    private CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires
    };
}
