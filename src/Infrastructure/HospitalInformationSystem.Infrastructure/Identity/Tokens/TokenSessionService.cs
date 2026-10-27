using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalInformationSystem.Infrastructure.Identity.Tokens;

public sealed class TokenSessionService(HospitalDbContext db, IAccountService accounts, JwtKeyMaterial keys,
    IOptions<JwtTokenOptions> options, TimeProvider timeProvider, IAuditWriter audit, IAuditActorAccessor actors)
{
    public async Task<TokenSessionResult?> IssueAsync(Guid userId, string expectedAuthenticationVersion,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        var session = user is null ? null : await accounts.GetSessionAsync(userId, cancellationToken);
        if (user is null || session is null || string.IsNullOrWhiteSpace(user.SecurityStamp) ||
            !string.Equals(user.SecurityStamp, expectedAuthenticationVersion, StringComparison.Ordinal))
            return null;
        var result = AddSession(user, session, Guid.NewGuid(), out _);
        audit.Append(session.Role == ApplicationRoles.Administrator ? AuditEventType.AdministratorLogin :
                AuditEventType.UserLogin, "Пользователь вошёл в систему.",
            new AuditMetadata { Action = "login", AccountId = user.Id, TargetId = user.Id,
                MedicalWorkerId = session.MedicalWorkerId, Outcome = "success" },
            VerifiedActor(user));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TokenSessionResult?> RotateAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = HashRefreshToken(refreshToken);
        if (hash is null) return null;
        var userId = await db.RefreshSessions.AsNoTracking().Where(session => session.TokenHash == hash)
            .Select(session => (Guid?)session.UserId).SingleOrDefaultAsync(cancellationToken);
        if (userId is null) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId.Value, cancellationToken);
        var previous = await db.RefreshSessions.SingleOrDefaultAsync(session => session.TokenHash == hash, cancellationToken);
        if (user is null || previous is null) return null;
        await db.Entry(previous).ReloadAsync(cancellationToken);
        var session = await accounts.GetSessionAsync(user.Id, cancellationToken);
        if (previous.RevokedAtUtc is not null || previous.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
            session is null || string.IsNullOrWhiteSpace(user.SecurityStamp) ||
            !string.Equals(previous.AuthenticationVersion, user.SecurityStamp, StringComparison.Ordinal))
        {
            var reason = previous.RevokedAtUtc is not null ? "refresh-reuse" : "refresh-rejected";
            var revoked = await RevokeFamilyRowsAsync(user.Id, previous.FamilyId, reason, cancellationToken);
            audit.Append(AuditEventType.AccountChanged, "Отозвано семейство сессии после отказа обновления.",
                new AuditMetadata { Action = reason, AccountId = user.Id, TargetId = user.Id,
                    RevokeReason = reason, RevokedRefreshTokenCount = revoked });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var result = AddSession(user, session, previous.FamilyId, out var replacementId);
        previous.RevokedAtUtc = timeProvider.GetUtcNow();
        previous.RevokeReason = "rotated";
        previous.ReplacedBySessionId = replacementId;
        audit.Append(AuditEventType.SessionRefreshed, "Обновлена сессия пользователя.",
            new AuditMetadata { Action = "refresh", AccountId = user.Id, TargetId = user.Id }, VerifiedActor(user));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> RevokeFamilyAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!TryReadIdentifier(principal, SecurityTokenClaimTypes.Subject, out var userId) ||
            !TryReadIdentifier(principal, SecurityTokenClaimTypes.SessionFamily, out var familyId))
            return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockUserAsync(userId, cancellationToken) is null) return 0;
        var revoked = await RevokeFamilyRowsAsync(userId, familyId, "logout", cancellationToken);
        audit.Append(AuditEventType.UserLogout, "Завершена сессия пользователя.",
            new AuditMetadata { Action = "logout", AccountId = userId, TargetId = userId,
                RevokeReason = "logout", RevokedRefreshTokenCount = revoked });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revoked;
    }

    public async Task<int> RevokeFamilyAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = HashRefreshToken(refreshToken);
        if (hash is null) return 0;
        var known = await db.RefreshSessions.AsNoTracking().Where(item => item.TokenHash == hash)
            .Select(item => new { item.UserId, item.FamilyId }).SingleOrDefaultAsync(cancellationToken);
        if (known is null) return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(known.UserId, cancellationToken);
        if (user is null) return 0;
        var credential = await db.RefreshSessions.AsNoTracking().SingleAsync(item => item.TokenHash == hash,
            cancellationToken);
        var verified = credential.RevokedAtUtc is null && credential.ExpiresAtUtc > timeProvider.GetUtcNow() &&
            string.Equals(credential.AuthenticationVersion, user.SecurityStamp, StringComparison.Ordinal) &&
            await accounts.GetSessionAsync(user.Id, cancellationToken) is not null;
        var revoked = await RevokeFamilyRowsAsync(known.UserId, known.FamilyId, "logout", cancellationToken);
        audit.Append(AuditEventType.UserLogout, "Завершена сессия пользователя.",
            new AuditMetadata { Action = "logout", AccountId = known.UserId, TargetId = known.UserId,
                RevokeReason = "logout", RevokedRefreshTokenCount = revoked },
            verified ? VerifiedActor(user) : actors.GetCurrentActor());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revoked;
    }

    public async Task<bool> ValidatePrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!TryReadIdentifier(principal, SecurityTokenClaimTypes.Subject, out var userId) ||
            !TryReadIdentifier(principal, SecurityTokenClaimTypes.SessionFamily, out var familyId) ||
            !TryReadIdentifier(principal, SecurityTokenClaimTypes.SessionId, out var sessionId))
            return false;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        var session = await accounts.GetSessionAsync(userId, cancellationToken);
        if (user is null || session is null || string.IsNullOrWhiteSpace(user.SecurityStamp) ||
            !HasSingleClaim(principal, SecurityTokenClaimTypes.AuthenticationVersion, user.SecurityStamp) ||
            !HasSingleClaim(principal, SecurityTokenClaimTypes.AccountRole, session.Role) ||
            !HasSingleClaim(principal, SecurityTokenClaimTypes.UserName, session.UserName))
            return false;
        if (session.MedicalWorkerId is { } workerId)
        {
            if (!HasSingleClaim(principal, AuthClaimTypes.MedicalWorkerId, workerId.ToString()) ||
                !HasSingleClaim(principal, SecurityTokenClaimTypes.MedicalRole, MedicalRoleName(session.MedicalWorkerRole)))
                return false;
        }
        else if (principal.HasClaim(claim => claim.Type is AuthClaimTypes.MedicalWorkerId or SecurityTokenClaimTypes.MedicalRole))
            return false;
        var now = timeProvider.GetUtcNow();
        return await db.RefreshSessions.AsNoTracking().AnyAsync(item => item.Id == sessionId && item.UserId == userId &&
            item.FamilyId == familyId && item.RevokedAtUtc == null && item.ExpiresAtUtc > now, cancellationToken);
    }

    private TokenSessionResult AddSession(HospitalUser user, SessionDto session, Guid familyId, out Guid sessionId)
    {
        var now = timeProvider.GetUtcNow();
        var issued = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var accessExpires = issued.AddMinutes(options.Value.AccessTokenMinutes);
        var refreshExpires = now.AddDays(options.Value.RefreshTokenDays);
        var refresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var refreshSession = new RefreshSessionDbo
        {
            Id = Guid.NewGuid(), UserId = user.Id, FamilyId = familyId,
            TokenHash = HashRefreshToken(refresh)!, AuthenticationVersion = user.SecurityStamp,
            CreatedAtUtc = now, ExpiresAtUtc = refreshExpires
        };
        db.RefreshSessions.Add(refreshSession);
        sessionId = refreshSession.Id;
        List<Claim> claims =
        [
            new(SecurityTokenClaimTypes.Subject, user.Id.ToString()),
            new(SecurityTokenClaimTypes.UserName, session.UserName),
            new(SecurityTokenClaimTypes.AccountRole, session.Role),
            new(SecurityTokenClaimTypes.AuthenticationVersion, user.SecurityStamp!),
            new(SecurityTokenClaimTypes.SessionFamily, familyId.ToString()),
            new(SecurityTokenClaimTypes.SessionId, refreshSession.Id.ToString()),
            new(JwtRegisteredClaimNames.Iat, issued.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64)
        ];
        if (session.MedicalWorkerId is { } workerId)
        {
            claims.Add(new Claim(AuthClaimTypes.MedicalWorkerId, workerId.ToString()));
            claims.Add(new Claim(SecurityTokenClaimTypes.MedicalRole, MedicalRoleName(session.MedicalWorkerRole)));
        }
        var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience, claims,
            issued.UtcDateTime, accessExpires.UtcDateTime, keys.SigningCredentials);
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        return new TokenSessionResult(new AuthenticationTokensDto(encoded, accessExpires,
            new AuthenticationSessionDto(session.Id, session.UserName, session.Role,
                session.MedicalWorkerId, session.MedicalWorkerRole)), refresh, refreshExpires);
    }

    private Task<HospitalUser?> LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE").AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    private AuditActor VerifiedActor(HospitalUser user) => actors.GetCurrentActor() with
    {
        Kind = AuditActorKind.User, UserId = user.Id, DisplayName = user.UserName!, Component = null
    };

    private async Task<int> RevokeFamilyRowsAsync(Guid userId, Guid familyId, string reason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sessions = await db.RefreshSessions.Where(item => item.UserId == userId && item.FamilyId == familyId &&
            item.RevokedAtUtc == null && item.ExpiresAtUtc > now).ToArrayAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevokeReason = reason;
        }
        return sessions.Length;
    }

    private static string? HashRefreshToken(string? refreshToken) => refreshToken is null || refreshToken.Length != 43 ||
        refreshToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
        ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    private static bool TryReadIdentifier(ClaimsPrincipal principal, string type, out Guid id)
    {
        id = Guid.Empty;
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && Guid.TryParse(claims[0].Value, out id) && id != Guid.Empty;
    }

    private static bool HasSingleClaim(ClaimsPrincipal principal, string type, string expected)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && string.Equals(claims[0].Value, expected, StringComparison.Ordinal);
    }

    private static string MedicalRoleName(MedicalWorkerRole? role) => role switch
    {
        MedicalWorkerRole.Doctor => "doctor",
        MedicalWorkerRole.Nurse => "nurse",
        MedicalWorkerRole.LaboratoryTechnician => "laboratory-technician",
        MedicalWorkerRole.Other => "other",
        _ => throw new InvalidOperationException("Для медицинской сессии не задана роль сотрудника.")
    };
}
