using System.Security.Cryptography;
using System.Text;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AdministratorSetupService(
    HospitalDbContext db,
    UserManager<HospitalUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IDataProtectionProvider protection,
    TotpCodeService totp,
    IOptions<AdministratorSetupOptions> options,
    TimeProvider timeProvider,
    IAuditWriter audit,
    IAuditActorAccessor actors) : IAdministratorSetupService
{
    private readonly IDataProtector _protector = protection.CreateProtector("MedSoft.AdministratorTotp.v1");

    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        new(!await db.AdministratorSetupStates.AsNoTracking().AnyAsync(state => state.IsCompleted, cancellationToken));

    public async Task<SetupChallengeDto> BeginAsync(BeginSetupRequest request, string ownerToken,
        CancellationToken cancellationToken)
    {
        RequireOwner(ownerToken);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Password != request.ConfirmPassword)
            throw new DomainValidationException("Пароль и подтверждение не совпадают.");
        if (request.Password is null || request.Password.Length is < 12 or > 128)
            throw new DomainValidationException("Пароль должен содержать от 12 до 128 символов.");
        var settings = RequireSettings();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockSetupAsync(cancellationToken);
        await RequireIncompleteSetupAsync(cancellationToken);
        var administrator = await db.Users.SingleOrDefaultAsync(user => user.IsAdministrator, cancellationToken)
            ?? new HospitalUser
            {
                Id = Guid.NewGuid(), UserName = settings.AdministratorUserName,
                IsAdministrator = true, IsActive = true, LockoutEnabled = true
            };
        foreach (var validator in users.PasswordValidators)
            RequireIdentitySuccess(await validator.ValidateAsync(users, administrator, request.Password));
        var secret = totp.GenerateSecret();
        var now = timeProvider.GetUtcNow();
        var challenge = new AdministratorSetupChallengeDbo
        {
            ChallengeId = Guid.NewGuid(), AdministratorUserId = administrator.Id, UserName = administrator.UserName!,
            PasswordHash = users.PasswordHasher.HashPassword(administrator, request.Password),
            TotpSecretProtected = _protector.Protect(secret), CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(settings.ChallengeLifetime)
        };
        db.Set<AdministratorSetupChallengeDbo>().RemoveRange(
            await db.Set<AdministratorSetupChallengeDbo>().ToListAsync(cancellationToken));
        db.Set<AdministratorSetupChallengeDbo>().Add(challenge);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SetupChallengeDto(challenge.ChallengeId, secret,
            totp.GetAuthenticatorUri(secret, settings.Issuer, administrator.UserName!), challenge.ExpiresAtUtc);
    }

    public async Task ConfirmAsync(ConfirmSetupRequest request, string ownerToken, CancellationToken cancellationToken)
    {
        RequireOwner(ownerToken);
        ArgumentNullException.ThrowIfNull(request);
        var settings = RequireSettings();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockSetupAsync(cancellationToken);
        await RequireIncompleteSetupAsync(cancellationToken);
        var challenge = await db.Set<AdministratorSetupChallengeDbo>()
            .SingleOrDefaultAsync(item => item.ChallengeId == request.ChallengeId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (challenge is null || challenge.ExpiresAtUtc <= now || challenge.FailedAttempts >= settings.MaxChallengeAttempts)
            throw new DomainValidationException("Настройка истекла или исчерпаны попытки. Начните её заново.");
        var secret = _protector.Unprotect(challenge.TotpSecretProtected);
        var acceptedStep = totp.FindMatchingStep(secret, request.Code, now, settings.AllowedTotpWindow);
        if (acceptedStep is null)
        {
            challenge.FailedAttempts++;
            audit.Append(AuditEventType.LoginFailed, "Отклонён код подтверждения первоначальной настройки.",
                new AuditMetadata { Action = "administrator-setup", Outcome = "invalid-totp" });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new DomainValidationException("Код не подошёл. Проверьте приложение-аутентификатор.");
        }

        var administrator = await db.Users.SingleOrDefaultAsync(user => user.Id == challenge.AdministratorUserId,
            cancellationToken);
        var created = administrator is null;
        administrator ??= new HospitalUser
        {
            Id = challenge.AdministratorUserId, UserName = challenge.UserName, IsAdministrator = true,
            IsActive = true, LockoutEnabled = true, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        if (!administrator.IsAdministrator || administrator.MedicalWorkerId is not null)
            throw new BusinessConflictException("Учётная запись администратора изменилась. Начните настройку заново.");
        administrator.PasswordHash = challenge.PasswordHash;
        administrator.SecurityStamp = Guid.NewGuid().ToString("N");
        administrator.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        administrator.AccessFailedCount = 0;
        administrator.LockoutEnd = null;
        administrator.IsActive = true;
        administrator.TwoFactorEnabled = true;
        if (created)
        {
            RequireIdentitySuccess(await users.CreateAsync(administrator));
            if (!await roles.RoleExistsAsync(ApplicationRoles.Administrator))
                RequireIdentitySuccess(await roles.CreateAsync(new IdentityRole<Guid>(ApplicationRoles.Administrator)
                { Id = Guid.NewGuid() }));
            RequireIdentitySuccess(await users.AddToRoleAsync(administrator, ApplicationRoles.Administrator));
        }
        else
        {
            RequireIdentitySuccess(await users.UpdateAsync(administrator));
        }

        var state = await db.AdministratorSetupStates.SingleOrDefaultAsync(cancellationToken)
            ?? new AdministratorSetupStateDbo { CreatedAtUtc = now };
        if (db.Entry(state).State == EntityState.Detached) db.AdministratorSetupStates.Add(state);
        state.AdministratorUserId = administrator.Id;
        state.TotpSecretProtected = challenge.TotpSecretProtected;
        state.IsCompleted = true;
        state.CompletedAtUtc = now;
        state.UpdatedAtUtc = now;
        db.Set<AdministratorTotpVerificationDbo>().Add(new AdministratorTotpVerificationDbo
        {
            UserId = administrator.Id, LastAcceptedStep = acceptedStep.Value
        });
        var sessions = await db.RefreshSessions.Where(session => session.UserId == administrator.Id &&
            session.RevokedAtUtc == null && session.ExpiresAtUtc > now).ToArrayAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevokeReason = "administrator-setup";
        }
        var actor = new AuditActor(AuditActorKind.User, administrator.Id, administrator.UserName!,
            actors.GetCurrentActor().RequestId, null);
        var metadata = new AuditMetadata
        {
            Action = "administrator-setup", TargetId = administrator.Id, AccountId = administrator.Id,
            RevokedRefreshTokenCount = sessions.Length, RevokeReason = "administrator-setup", Outcome = "completed"
        };
        audit.Append(AuditEventType.AdministratorSetup, "Завершена первоначальная настройка администратора.", metadata, actor);
        audit.Append(created ? AuditEventType.AccountCreated : AuditEventType.AccountChanged,
            created ? "Создана учётная запись администратора." : "Установлены пароль и второй фактор администратора.",
            metadata, actor);
        db.Set<AdministratorSetupChallengeDbo>().Remove(challenge);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> VerifyAdministratorCodeAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        var settings = RequireSettings();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var verification = await db.Set<AdministratorTotpVerificationDbo>().FromSqlInterpolated(
            $"SELECT *, xmin FROM administrator_totp_verification WHERE user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (verification is null || verification.LockedUntilUtc > now) return false;
        var state = await db.AdministratorSetupStates.AsNoTracking().SingleOrDefaultAsync(
            item => item.IsCompleted && item.AdministratorUserId == userId, cancellationToken);
        if (state?.TotpSecretProtected is null || !await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId &&
            user.IsAdministrator && user.IsActive && (user.LockoutEnd == null || user.LockoutEnd <= now), cancellationToken))
            return false;
        if (verification.LockedUntilUtc is not null)
        {
            verification.LockedUntilUtc = null;
            verification.FailedAttempts = 0;
        }
        var acceptedStep = totp.FindMatchingStep(_protector.Unprotect(state.TotpSecretProtected), code, now,
            settings.AllowedTotpWindow);
        var accepted = acceptedStep is { } step && step > verification.LastAcceptedStep;
        if (accepted)
        {
            verification.LastAcceptedStep = acceptedStep!.Value;
            verification.FailedAttempts = 0;
        }
        else if (++verification.FailedAttempts >= settings.MaxTotpAttempts)
            verification.LockedUntilUtc = now.Add(settings.TotpLockout);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private Task<int> LockSetupAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1296385107, 1)", cancellationToken);

    private async Task RequireIncompleteSetupAsync(CancellationToken cancellationToken)
    {
        if (await db.AdministratorSetupStates.AnyAsync(state => state.IsCompleted, cancellationToken))
            throw new BusinessConflictException("Первоначальная настройка уже завершена.");
    }

    private void RequireOwner(string ownerToken)
    {
        var configured = options.Value.TokenSha256;
        if (string.IsNullOrEmpty(ownerToken) || ownerToken.Length is < 32 or > 512 || configured.Length != 64)
            throw new SetupAccessDeniedException();
        byte[] expected;
        try { expected = Convert.FromHexString(configured); }
        catch (FormatException) { throw new SetupAccessDeniedException(); }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(ownerToken));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new SetupAccessDeniedException();
    }

    private AdministratorSetupOptions RequireSettings()
    {
        var settings = options.Value;
        if (!AccountUserNameRules.IsValid(settings.AdministratorUserName) ||
            settings.ChallengeLifetime <= TimeSpan.Zero || settings.ChallengeLifetime > TimeSpan.FromMinutes(10) ||
            settings.MaxChallengeAttempts is < 1 or > 20 || settings.MaxTotpAttempts is < 1 or > 20 ||
            settings.TotpLockout <= TimeSpan.Zero || settings.TotpLockout > TimeSpan.FromHours(1) ||
            settings.AllowedTotpWindow is < 0 or > 1 || string.IsNullOrWhiteSpace(settings.Issuer) ||
            settings.Issuer.Length > 64 || settings.Issuer.Any(char.IsControl))
            throw new InvalidOperationException("Некорректные параметры первоначальной настройки и TOTP.");
        return settings;
    }

    private static void RequireIdentitySuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new DomainValidationException(string.Join(" ", result.Errors.Select(error => error.Description)));
    }
}
