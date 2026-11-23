using System.Security.Claims;
using HospitalInformationSystem.Abstractions.Identity;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalWorkers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AccountRepository(HospitalDbContext db, UserManager<HospitalUser> users) : IAccountRepository
{
    public async Task<IReadOnlyList<AccountIdentity>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await (from user in db.Users.AsNoTracking()
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            orderby user.UserName
            select new { User = user, Role = role.Name! }).ToArrayAsync(cancellationToken);
        return rows.GroupBy(row => row.User.Id)
            .Select(group => ToIdentity(group.First().User, group.Select(row => row.Role).ToArray()))
            .ToArray();
    }

    public async Task<AccountIdentity?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        return user is null ? null : ToIdentity(user, await GetRolesAsync(accountId, cancellationToken));
    }

    public async Task<AccountIdentity?> FindForUpdateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var user = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {accountId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : ToIdentity(user, await GetRolesAsync(accountId, cancellationToken));
    }

    public async Task<MedicalWorker?> FindMedicalWorkerForUpdateAsync(Guid medicalWorkerId,
        CancellationToken cancellationToken) =>
        (await db.MedicalWorkers.FromSqlInterpolated(
            $"SELECT *, xmin FROM medical_workers WHERE id = {medicalWorkerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken))?.ToDomain();

    public Task<bool> HasMedicalWorkerAccountAsync(Guid medicalWorkerId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(account => account.MedicalWorkerId == medicalWorkerId, cancellationToken);

    public Task<bool> HasAdministratorAsync(CancellationToken cancellationToken) =>
        db.Users.AnyAsync(account => account.IsAdministrator, cancellationToken);

    public async Task CreateAsync(AccountIdentity account, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = new HospitalUser
        {
            Id = account.Id, UserName = account.UserName, MedicalWorkerId = account.MedicalWorkerId,
            LockoutEnabled = true, IsAdministrator = account.IsAdministrator, IsActive = account.IsActive,
            CreatedAtUtc = account.CreatedAtUtc, UpdatedAtUtc = account.UpdatedAtUtc
        };
        RequireSuccess(await users.CreateAsync(user, password));
        foreach (var role in account.Roles)
            RequireSuccess(await users.AddToRoleAsync(user, role));
        if (account.MedicalWorkerId is { } medicalWorkerId)
            RequireSuccess(await users.AddClaimAsync(user,
                new Claim(AuthClaimTypes.MedicalWorkerId, medicalWorkerId.ToString())));
    }

    public async Task ResetPasswordAsync(Guid accountId, string password, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(item => item.Id == accountId, cancellationToken);
        foreach (var validator in users.PasswordValidators)
            RequireSuccess(await validator.ValidateAsync(users, user, password));
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        RenewStamps(user);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
    }

    public async Task SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(item => item.Id == accountId, cancellationToken);
        user.IsActive = isActive;
        RenewStamps(user);
    }

    public async Task<int> RevokeRefreshSessionsAsync(Guid accountId, string reason, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessions = await db.RefreshSessions.Where(session => session.UserId == accountId &&
            session.RevokedAtUtc == null && session.ExpiresAtUtc > now).ToArrayAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevokeReason = reason;
        }
        return sessions.Length;
    }

    private Task<string?[]> GetRolesAsync(Guid accountId, CancellationToken cancellationToken) =>
        (from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == accountId
            select role.Name).ToArrayAsync(cancellationToken);

    private static AccountIdentity ToIdentity(HospitalUser user, IEnumerable<string?> roles) =>
        new(user.Id, user.UserName, user.MedicalWorkerId, user.IsAdministrator, user.IsActive,
            user.LockoutEnd, user.CreatedAtUtc, user.UpdatedAtUtc, roles.Select(role => role!).ToArray());

    private static void RenewStamps(HospitalUser user)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (result.Succeeded) return;
        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.DuplicateUserName)))
            throw new BusinessConflictException("Этот логин уже используется.");
        throw new DomainValidationException(string.Join(" ", result.Errors.Select(error => error.Description)));
    }
}
