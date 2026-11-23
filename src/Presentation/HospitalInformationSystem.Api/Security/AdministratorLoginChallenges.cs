using System.Security.Cryptography;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace HospitalInformationSystem.Api.Security;

public sealed class AdministratorLoginChallenges(IDataProtectionProvider protection, TimeProvider time,
    IWebHostEnvironment environment) : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly MemoryCache _pending = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IDataProtector _protector = protection.CreateProtector("MedSoft.AdministratorLogin.v1");

    public string CookieName => environment.IsDevelopment() || environment.IsEnvironment("Testing")
        ? "MedSoft.AdministratorLogin" : "__Host-MedSoft.AdministratorLogin";

    public AdministratorLoginAttempt Create(HttpContext context, HospitalUser user)
    {
        Clear(context);
        var attempt = new AdministratorLoginAttempt(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            user.Id, user.UserName!, user.SecurityStamp!, time.GetUtcNow().Add(Lifetime));
        lock (_gate)
            _pending.Set(attempt.Id, attempt, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Lifetime,
                Size = 1
            });
        context.Response.Cookies.Append(BrowserTabCookieNames.ForRequest(context, CookieName),
            _protector.Protect(attempt.Id), Options(attempt.ExpiresAtUtc));
        return attempt;
    }

    public AdministratorLoginAttempt? Read(HttpContext context)
    {
        var id = ReadId(context);
        if (id is null) return null;
        lock (_gate) return Find(id);
    }

    public AdministratorLoginAttempt? Acquire(HttpContext context)
    {
        var id = ReadId(context);
        if (id is null) return null;
        lock (_gate)
        {
            var attempt = Find(id);
            return attempt is not null && _inFlight.Add(id) ? attempt : null;
        }
    }

    public bool Consume(AdministratorLoginAttempt attempt)
    {
        lock (_gate)
        {
            if (Find(attempt.Id) is null) return false;
            _pending.Remove(attempt.Id);
            return true;
        }
    }

    public bool RejectCode(AdministratorLoginAttempt attempt)
    {
        lock (_gate)
        {
            var current = Find(attempt.Id);
            if (current is null) return false;
            if (current.FailedAttempts >= 4)
            {
                _pending.Remove(attempt.Id);
                return false;
            }
            var remaining = current.ExpiresAtUtc - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                _pending.Remove(attempt.Id);
                return false;
            }
            _pending.Set(attempt.Id, current with { FailedAttempts = current.FailedAttempts + 1 },
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = remaining,
                    Size = 1
                });
            return true;
        }
    }

    public void Release(AdministratorLoginAttempt attempt)
    {
        lock (_gate) _inFlight.Remove(attempt.Id);
    }

    public void Clear(HttpContext context)
    {
        var id = ReadId(context);
        if (id is not null)
            lock (_gate) _pending.Remove(id);
        context.Response.Cookies.Delete(BrowserTabCookieNames.ForRequest(context, CookieName), Options(null));
    }

    private AdministratorLoginAttempt? Find(string id)
    {
        if (!_pending.TryGetValue<AdministratorLoginAttempt>(id, out var attempt)) return null;
        if (attempt!.ExpiresAtUtc > time.GetUtcNow()) return attempt;
        _pending.Remove(id);
        return null;
    }

    private string? ReadId(HttpContext context)
    {
        if (context.Request.Cookies[BrowserTabCookieNames.ForRequest(context, CookieName)] is not
            { Length: > 0 and <= 2048 } value) return null;
        try
        {
            var id = _protector.Unprotect(value);
            return id.Length == 64 && id.All(Uri.IsHexDigit) ? id : null;
        }
        catch (CryptographicException) { return null; }
    }

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires
    };

    public void Dispose() => _pending.Dispose();
}
