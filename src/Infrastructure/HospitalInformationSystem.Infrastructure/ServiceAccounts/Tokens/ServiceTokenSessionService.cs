using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;

public sealed partial class ServiceTokenSessionService(HospitalDbContext db, JwtKeyMaterial keys,
    IOptions<ServiceTokenOptions> options, TimeProvider timeProvider, IAuditWriter audit, IAuditActorAccessor actors,
    IServiceApiKeyGenerator apiKeys)
{
    public async Task<ServiceTokenPairDto?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (!ServiceApiKeyGenerator.TryGetAccountId(apiKey, out var id))
            return await RejectAsync("service-token-rejected", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await LockAccountAsync(id, cancellationToken);
        var verification = account is not null && Eligible(account)
            ? apiKeys.Verify(apiKey, id, account.ApiKeyHash)
            : ServiceApiKeyVerificationResult.Failed;
        if (verification == ServiceApiKeyVerificationResult.Failed)
        {
            await RejectAsync("service-token-rejected", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (verification == ServiceApiKeyVerificationResult.SuccessRehashNeeded)
        {
            var hash = apiKeys.Hash(apiKey!);
            await db.ServiceAccounts.Where(row => row.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ApiKeyHash, hash), cancellationToken);
        }
        var now = Timestamp(timeProvider.GetUtcNow());
        var expires = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).AddDays(options.Value.RefreshTokenDays);
        var result = AddSession(account!, Guid.NewGuid(), now, expires, out _);
        audit.Append(AuditEventType.ServiceLogin, "Сервисный аккаунт LIS получил сессию.",
            Metadata(account!.Id, "service-token-issued", "success"), VerifiedActor(account));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ServiceTokenPairDto?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        var hash = ServiceRefreshTokenMaterial.Hash(refreshToken);
        if (hash is null) return await RejectAsync("service-refresh-rejected", cancellationToken);
        var owner = await db.ServiceRefreshSessions.AsNoTracking().Where(row => row.TokenHash == hash)
            .Select(row => (Guid?)row.ServiceAccountId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return await RejectAsync("service-refresh-rejected", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await LockAccountAsync(owner.Value, cancellationToken);
        var previous = await db.ServiceRefreshSessions.FromSqlInterpolated(
                $"SELECT *, xmin FROM service_refresh_sessions WHERE token_hash = {hash} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (account is null || previous is null || previous.ServiceAccountId != account.Id)
        {
            await RejectAsync("service-refresh-rejected", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var now = Timestamp(timeProvider.GetUtcNow());
        var familyRevoked = await db.ServiceRefreshSessions.AsNoTracking().AnyAsync(row =>
            row.ServiceAccountId == account.Id && row.FamilyId == previous.FamilyId && row.RevokedAtUtc != null,
            cancellationToken);
        if (previous.ConsumedAtUtc is not null || previous.RevokedAtUtc is not null || familyRevoked ||
            previous.ExpiresAtUtc <= now || previous.CreatedAtUtc > now || !Eligible(account) ||
            previous.AuthenticationVersion != account.AuthenticationVersion)
        {
            var reason = previous.ConsumedAtUtc is not null ? "service-refresh-reuse" : "service-refresh-rejected";
            var revoked = await RevokeFamilyAsync(account.Id, previous.FamilyId, now, reason, cancellationToken);
            audit.Append(AuditEventType.LoginFailed, "Отказано в обновлении сервисной сессии LIS.",
                Metadata(account.Id, reason, "failure") with
                { RevokeReason = reason, RevokedServiceSessionCount = revoked }, AnonymousActor());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var result = AddSession(account, previous.FamilyId, now, previous.ExpiresAtUtc, out var replacement);
        audit.Append(AuditEventType.SessionRefreshed, "Обновлена сервисная сессия LIS.",
            Metadata(account.Id, "service-session-refreshed", "success"), VerifiedActor(account));
        await db.SaveChangesAsync(cancellationToken);
        var changed = await db.ServiceRefreshSessions.Where(row => row.Id == previous.Id &&
                row.ConsumedAtUtc == null && row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ConsumedAtUtc, now)
                .SetProperty(row => row.ReplacedBySessionId, replacement), cancellationToken);
        if (changed != 1) throw new InvalidOperationException("Сервисная сессия изменилась при обновлении.");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private ServiceTokenPairDto AddSession(ServiceAccountDbo account, Guid familyId, DateTimeOffset now,
        DateTimeOffset familyExpires, out Guid sessionId)
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var expires = issued.AddMinutes(options.Value.AccessTokenMinutes);
        if (expires > familyExpires) expires = familyExpires;
        var refresh = ServiceRefreshTokenMaterial.Generate();
        sessionId = Guid.NewGuid();
        db.ServiceRefreshSessions.Add(new ServiceRefreshSessionDbo
        {
            Id = sessionId, ServiceAccountId = account.Id, FamilyId = familyId,
            TokenHash = ServiceRefreshTokenMaterial.Hash(refresh)!, AuthenticationVersion = account.AuthenticationVersion,
            CreatedAtUtc = now, ExpiresAtUtc = familyExpires
        });
        List<Claim> claims =
        [
            new(ServiceAuthenticationDefaults.Claims.Subject, account.Id.ToString("D")),
            new(ServiceAuthenticationDefaults.Claims.AccountKind, ServiceAuthenticationDefaults.AccountKind),
            new(ServiceAuthenticationDefaults.Claims.Role, ServiceAuthenticationDefaults.Role),
            new(ServiceAuthenticationDefaults.Claims.AuthenticationVersion,
                account.AuthenticationVersion.ToString(CultureInfo.InvariantCulture)),
            new(ServiceAuthenticationDefaults.Claims.SessionId, familyId.ToString("D")),
            new(ServiceAuthenticationDefaults.Claims.TokenId, sessionId.ToString("D")),
            new(ServiceAuthenticationDefaults.Claims.IssuedAt,
                issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("name", account.Name)
        ];
        var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience, claims,
            issued.UtcDateTime, expires.UtcDateTime, keys.SigningCredentials);
        return new ServiceTokenPairDto(new JwtSecurityTokenHandler().WriteToken(token), expires, refresh, familyExpires);
    }

    private Task<ServiceAccountDbo?> LockAccountAsync(Guid id, CancellationToken cancellationToken) =>
        db.ServiceAccounts.FromSqlInterpolated($"SELECT *, xmin FROM service_accounts WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    private async Task<int> RevokeFamilyAsync(Guid accountId, Guid familyId, DateTimeOffset now, string reason,
        CancellationToken cancellationToken)
    {
        var family = db.ServiceRefreshSessions.Where(row => row.ServiceAccountId == accountId &&
            row.FamilyId == familyId && row.RevokedAtUtc == null);
        var active = await family.AnyAsync(row => row.ExpiresAtUtc > now, cancellationToken);
        await family.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAtUtc,
                row => row.CreatedAtUtc > now ? row.CreatedAtUtc : now)
            .SetProperty(row => row.RevokeReason, reason), cancellationToken);
        return active ? 1 : 0;
    }

    private async Task<ServiceTokenPairDto?> RejectAsync(string action, CancellationToken cancellationToken)
    {
        audit.Append(AuditEventType.LoginFailed, "Не удалось подтвердить сервисные учётные данные LIS.",
            new AuditMetadata { Action = action, Outcome = "failure" }, AnonymousActor());
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private AuditActor VerifiedActor(ServiceAccountDbo account) => new(AuditActorKind.ServiceAccount, null,
        account.Name, RequestId(), null, account.Id);

    private AuditActor AnonymousActor() => new(AuditActorKind.Anonymous, null, "Анонимный пользователь",
        RequestId(), null);

    private string RequestId()
    {
        var requestId = actors.GetCurrentActor().RequestId;
        return string.IsNullOrWhiteSpace(requestId) ? "service-auth" : requestId;
    }

    private static AuditMetadata Metadata(Guid id, string action, string outcome) => new()
    { Action = action, TargetId = id, ServiceAccountId = id, Outcome = outcome };

    private static bool Eligible(ServiceAccountDbo account) => account.IsActive &&
        account.Role == ServiceAccountRole.Lis && account.AuthenticationVersion > 0;

    private static DateTimeOffset Timestamp(DateTimeOffset timestamp)
    {
        var ticks = timestamp.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}
