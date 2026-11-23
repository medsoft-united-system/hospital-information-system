using HospitalInformationSystem.Abstractions.Identity;
using HospitalInformationSystem.Abstractions.Transactions;
using HospitalInformationSystem.Abstractions.UnitOfWork;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Application.Auth.Services;

public sealed class AdministratorSetupService(
    IAdministratorSetupStore store,
    IAccountRepository accounts,
    IAdministratorSetupCryptography cryptography,
    AdministratorSetupOptions options,
    IUnitOfWork unitOfWork,
    ITransactionManager transactions,
    TimeProvider timeProvider,
    IAuditWriter audit,
    IAuditActorAccessor actors) : IAdministratorSetupService
{
    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        new(!await store.IsCompletedAsync(cancellationToken));

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
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        await LockSetupAsync(cancellationToken);
        await RequireIncompleteSetupAsync(cancellationToken);
        var administrator = await store.FindAdministratorAsync(cancellationToken)
            ?? new AdministratorSetupAccount(Guid.NewGuid(), settings.AdministratorUserName,
                true, null, true, false, default, default);
        var passwordHash = await store.HashPasswordAsync(administrator, request.Password, cancellationToken);
        var secret = cryptography.GenerateSecret();
        var now = timeProvider.GetUtcNow();
        var challenge = new AdministratorSetupChallengeDbo
        {
            ChallengeId = Guid.NewGuid(), AdministratorUserId = administrator.Id, UserName = administrator.UserName!,
            PasswordHash = passwordHash,
            TotpSecretProtected = cryptography.Protect(secret), CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(settings.ChallengeLifetime)
        };
        await store.ReplaceChallengesAsync(challenge, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SetupChallengeDto(challenge.ChallengeId, secret,
            cryptography.GetAuthenticatorUri(secret, settings.Issuer, administrator.UserName!), challenge.ExpiresAtUtc);
    }

    public async Task ConfirmAsync(ConfirmSetupRequest request, string ownerToken, CancellationToken cancellationToken)
    {
        RequireOwner(ownerToken);
        ArgumentNullException.ThrowIfNull(request);
        var settings = RequireSettings();
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        await LockSetupAsync(cancellationToken);
        await RequireIncompleteSetupAsync(cancellationToken);
        var challenge = await store.FindChallengeAsync(request.ChallengeId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (challenge is null || challenge.ExpiresAtUtc <= now || challenge.FailedAttempts >= settings.MaxChallengeAttempts)
            throw new DomainValidationException("Настройка истекла или исчерпаны попытки. Начните её заново.");
        var secret = cryptography.Unprotect(challenge.TotpSecretProtected);
        var acceptedStep = cryptography.FindMatchingStep(secret, request.Code, now, settings.AllowedTotpWindow);
        if (acceptedStep is null)
        {
            challenge.FailedAttempts++;
            audit.Append(AuditEventType.LoginFailed, "Отклонён код подтверждения первоначальной настройки.",
                new AuditMetadata { Action = "administrator-setup", Outcome = "invalid-totp" });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new DomainValidationException("Код не подошёл. Проверьте приложение-аутентификатор.");
        }

        var administrator = await store.FindAccountAsync(challenge.AdministratorUserId, cancellationToken);
        var created = administrator is null;
        administrator ??= new AdministratorSetupAccount(challenge.AdministratorUserId, challenge.UserName,
            true, null, true, true, now, now);
        if (!administrator.IsAdministrator || administrator.MedicalWorkerId is not null)
            throw new BusinessConflictException("Учётная запись администратора изменилась. Начните настройку заново.");
        administrator = administrator with { IsActive = true, TwoFactorEnabled = true };
        await store.SaveAccountAsync(administrator, challenge.PasswordHash, created, ApplicationRoles.Administrator,
            cancellationToken);

        var state = await store.FindStateAsync(cancellationToken);
        if (state is null)
        {
            state = new AdministratorSetupStateDbo { CreatedAtUtc = now };
            store.AddState(state);
        }
        state.AdministratorUserId = administrator.Id;
        state.TotpSecretProtected = challenge.TotpSecretProtected;
        state.IsCompleted = true;
        state.CompletedAtUtc = now;
        state.UpdatedAtUtc = now;
        store.AddVerification(new AdministratorTotpVerificationDbo
        {
            UserId = administrator.Id, LastAcceptedStep = acceptedStep.Value
        });
        var revoked = await accounts.RevokeRefreshSessionsAsync(administrator.Id, "administrator-setup", now,
            cancellationToken);
        var actor = new AuditActor(AuditActorKind.User, administrator.Id, administrator.UserName!,
            actors.GetCurrentActor().RequestId, null);
        var metadata = new AuditMetadata
        {
            Action = "administrator-setup", TargetId = administrator.Id, AccountId = administrator.Id,
            RevokedRefreshTokenCount = revoked, RevokeReason = "administrator-setup", Outcome = "completed"
        };
        audit.Append(AuditEventType.AdministratorSetup, "Завершена первоначальная настройка администратора.", metadata, actor);
        audit.Append(created ? AuditEventType.AccountCreated : AuditEventType.AccountChanged,
            created ? "Создана учётная запись администратора." : "Установлены пароль и второй фактор администратора.",
            metadata, actor);
        store.RemoveChallenge(challenge);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> VerifyAdministratorCodeAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        var settings = RequireSettings();
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        var verification = await store.FindVerificationForUpdateAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (verification is null || verification.LockedUntilUtc > now) return false;
        var state = await store.FindCompletedStateAsync(userId, cancellationToken);
        if (state?.TotpSecretProtected is null ||
            !await store.IsAdministratorAvailableAsync(userId, now, cancellationToken))
            return false;
        if (verification.LockedUntilUtc is not null)
        {
            verification.LockedUntilUtc = null;
            verification.FailedAttempts = 0;
        }
        var acceptedStep = cryptography.FindMatchingStep(cryptography.Unprotect(state.TotpSecretProtected), code, now,
            settings.AllowedTotpWindow);
        var accepted = acceptedStep is { } step && step > verification.LastAcceptedStep;
        if (accepted)
        {
            verification.LastAcceptedStep = acceptedStep!.Value;
            verification.FailedAttempts = 0;
        }
        else if (++verification.FailedAttempts >= settings.MaxTotpAttempts)
            verification.LockedUntilUtc = now.Add(settings.TotpLockout);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private Task LockSetupAsync(CancellationToken cancellationToken) => store.LockSetupAsync(cancellationToken);

    private async Task RequireIncompleteSetupAsync(CancellationToken cancellationToken)
    {
        if (await store.IsCompletedAsync(cancellationToken))
            throw new BusinessConflictException("Первоначальная настройка уже завершена.");
    }

    private void RequireOwner(string ownerToken)
    {
        if (!cryptography.VerifyOwnerToken(ownerToken, options.TokenSha256))
            throw new SetupAccessDeniedException();
    }

    private AdministratorSetupOptions RequireSettings()
    {
        var settings = options;
        if (!AccountUserNameRules.IsValid(settings.AdministratorUserName) ||
            settings.ChallengeLifetime <= TimeSpan.Zero || settings.ChallengeLifetime > TimeSpan.FromMinutes(10) ||
            settings.MaxChallengeAttempts is < 1 or > 20 || settings.MaxTotpAttempts is < 1 or > 20 ||
            settings.TotpLockout <= TimeSpan.Zero || settings.TotpLockout > TimeSpan.FromHours(1) ||
            settings.AllowedTotpWindow is < 0 or > 1 || string.IsNullOrWhiteSpace(settings.Issuer) ||
            settings.Issuer.Length > 64 || settings.Issuer.Any(char.IsControl))
            throw new InvalidOperationException("Некорректные параметры первоначальной настройки и TOTP.");
        return settings;
    }
}
