using HospitalInformationSystem.Application.Abstractions.Identity;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AdministratorSetupStore(HospitalDbContext db, UserManager<HospitalUser> users,
    RoleManager<IdentityRole<Guid>> roles) : IAdministratorSetupStore
{
    public Task<bool> IsCompletedAsync(CancellationToken cancellationToken) =>
        db.AdministratorSetupStates.AsNoTracking().AnyAsync(state => state.IsCompleted, cancellationToken);

    public async Task LockSetupAsync(CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1296385107, 1)", cancellationToken);

    public async Task<AdministratorSetupAccount?> FindAdministratorAsync(CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(account => account.IsAdministrator, cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public async Task<AdministratorSetupAccount?> FindAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(account => account.Id == userId, cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public async Task<string> HashPasswordAsync(AdministratorSetupAccount account, string password,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == account.Id, cancellationToken)
            ?? NewUser(account);
        foreach (var validator in users.PasswordValidators)
            RequireSuccess(await validator.ValidateAsync(users, user, password));
        return users.PasswordHasher.HashPassword(user, password);
    }

    public async Task ReplaceChallengesAsync(AdministratorSetupChallengeDbo challenge, CancellationToken cancellationToken)
    {
        db.Set<AdministratorSetupChallengeDbo>().RemoveRange(
            await db.Set<AdministratorSetupChallengeDbo>().ToListAsync(cancellationToken));
        db.Set<AdministratorSetupChallengeDbo>().Add(challenge);
    }

    public Task<AdministratorSetupChallengeDbo?> FindChallengeAsync(Guid challengeId, CancellationToken cancellationToken) =>
        db.Set<AdministratorSetupChallengeDbo>().SingleOrDefaultAsync(item => item.ChallengeId == challengeId,
            cancellationToken);

    public void RemoveChallenge(AdministratorSetupChallengeDbo challenge) =>
        db.Set<AdministratorSetupChallengeDbo>().Remove(challenge);

    public async Task SaveAccountAsync(AdministratorSetupAccount account, string passwordHash, bool created, string role,
        CancellationToken cancellationToken)
    {
        var user = created ? NewUser(account)
            : await db.Users.SingleAsync(item => item.Id == account.Id, cancellationToken);
        user.PasswordHash = passwordHash;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.IsActive = account.IsActive;
        user.TwoFactorEnabled = account.TwoFactorEnabled;
        if (created)
        {
            RequireSuccess(await users.CreateAsync(user));
            if (!await roles.RoleExistsAsync(role))
                RequireSuccess(await roles.CreateAsync(new IdentityRole<Guid>(role)
                { Id = Guid.NewGuid() }));
            RequireSuccess(await users.AddToRoleAsync(user, role));
        }
        else
            RequireSuccess(await users.UpdateAsync(user));
    }

    public Task<AdministratorSetupStateDbo?> FindStateAsync(CancellationToken cancellationToken) =>
        db.AdministratorSetupStates.SingleOrDefaultAsync(cancellationToken);

    public Task<AdministratorSetupStateDbo?> FindCompletedStateAsync(Guid userId, CancellationToken cancellationToken) =>
        db.AdministratorSetupStates.AsNoTracking().SingleOrDefaultAsync(
            state => state.IsCompleted && state.AdministratorUserId == userId, cancellationToken);

    public void AddState(AdministratorSetupStateDbo state) => db.AdministratorSetupStates.Add(state);

    public void AddVerification(AdministratorTotpVerificationDbo verification) =>
        db.Set<AdministratorTotpVerificationDbo>().Add(verification);

    public Task<AdministratorTotpVerificationDbo?> FindVerificationForUpdateAsync(Guid userId,
        CancellationToken cancellationToken) =>
        db.Set<AdministratorTotpVerificationDbo>().FromSqlInterpolated(
            $"SELECT *, xmin FROM administrator_totp_verification WHERE user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> IsAdministratorAvailableAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsAdministrator && user.IsActive &&
            (user.LockoutEnd == null || user.LockoutEnd <= now), cancellationToken);

    private static HospitalUser NewUser(AdministratorSetupAccount account) => new()
    {
        Id = account.Id,
        UserName = account.UserName,
        IsAdministrator = account.IsAdministrator,
        MedicalWorkerId = account.MedicalWorkerId,
        IsActive = account.IsActive,
        LockoutEnabled = true,
        TwoFactorEnabled = account.TwoFactorEnabled,
        CreatedAtUtc = account.CreatedAtUtc,
        UpdatedAtUtc = account.UpdatedAtUtc
    };

    private static AdministratorSetupAccount ToAccount(HospitalUser user) =>
        new(user.Id, user.UserName!, user.IsAdministrator, user.MedicalWorkerId, user.IsActive,
            user.TwoFactorEnabled, user.CreatedAtUtc, user.UpdatedAtUtc);

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new DomainValidationException(string.Join(" ", result.Errors.Select(error => error.Description)));
    }
}
