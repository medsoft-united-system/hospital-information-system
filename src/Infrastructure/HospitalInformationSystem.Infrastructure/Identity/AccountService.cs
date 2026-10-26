using System.Security.Claims;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MedicalWorkerRole = HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class AccountService(
    HospitalDbContext db,
    UserManager<HospitalUser> users,
    TimeProvider timeProvider,
    IAccountPasswordGenerator passwordGenerator,
    IAuditWriter audit) : IAccountService
{
    public async Task<IReadOnlyList<AccountDetailsDto>> ListDetailsAsync(CancellationToken cancellationToken)
    {
        var accounts = await ListAsync(cancellationToken);
        var workers = await db.MedicalWorkers.AsNoTracking().ToDictionaryAsync(worker => worker.Id, cancellationToken);
        return accounts.Select(account => ToDetails(account,
            account.MedicalWorkerId is { } workerId && workers.TryGetValue(workerId, out var worker) ? worker : null))
            .ToArray();
    }

    public async Task<CreateWorkerAccountResultDto> CreateForWorkerAsync(Guid medicalWorkerId,
        CreateWorkerAccountRequest request, CancellationToken cancellationToken)
    {
        var password = passwordGenerator.Generate();
        var account = await CreateAsync(new CreateAccountRequest(request.UserName, password,
            ApplicationRoles.MedicalWorker, medicalWorkerId), cancellationToken);
        var worker = await db.MedicalWorkers.AsNoTracking().SingleAsync(item => item.Id == medicalWorkerId,
            cancellationToken);
        return new CreateWorkerAccountResultDto(ToDetails(account, worker), password);
    }

    public async Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await (from user in db.Users.AsNoTracking()
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            orderby user.UserName
            select new AccountDto(user.Id, user.UserName!, role.Name!, user.MedicalWorkerId,
                user.LockoutEnd != null && user.LockoutEnd > now, user.IsActive,
                user.CreatedAtUtc, user.UpdatedAtUtc)).ToArrayAsync(cancellationToken);
    }

    public async Task<AccountDto> CreateAsync(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var name = request.UserName?.Trim();
        if (!AccountUserNameRules.IsValid(name))
            throw new DomainValidationException("Логин должен содержать от 3 до 16 разрешённых латинских символов.");
        if (request.Password is null || request.Password.Length is < 12 or > 128)
            throw new DomainValidationException("Пароль должен содержать от 12 до 128 символов.");
        if (request.Role is not (ApplicationRoles.Administrator or ApplicationRoles.MedicalWorker))
            throw new DomainValidationException("Неизвестная роль учётной записи.");
        if (request.Role == ApplicationRoles.Administrator && request.MedicalWorkerId is not null)
            throw new DomainValidationException("Для администратора нельзя указывать медработника.");
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (request.Role == ApplicationRoles.MedicalWorker)
        {
            if (request.MedicalWorkerId is not { } workerId)
                throw new DomainValidationException("Выберите действующего медработника.");
            var worker = await db.MedicalWorkers.FromSqlInterpolated(
                $"SELECT *, xmin FROM medical_workers WHERE id = {workerId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
            if (worker is null || worker.IsDeleted)
                throw new DomainValidationException("Выберите действующего медработника.");
            if (await db.Users.AnyAsync(account => account.MedicalWorkerId == workerId, cancellationToken))
                throw new BusinessConflictException("У медработника уже есть учётная запись.");
        }
        if (request.Role == ApplicationRoles.Administrator &&
            await db.Users.AnyAsync(account => account.IsAdministrator, cancellationToken))
            throw new BusinessConflictException("В системе уже существует администратор.");
        var now = timeProvider.GetUtcNow();
        var user = new HospitalUser
        {
            Id = Guid.NewGuid(), UserName = name, MedicalWorkerId = request.MedicalWorkerId,
            LockoutEnabled = true, IsAdministrator = request.Role == ApplicationRoles.Administrator,
            IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        RequireSuccess(await users.CreateAsync(user, request.Password));
        RequireSuccess(await users.AddToRoleAsync(user, request.Role));
        if (user.MedicalWorkerId is { } medicalWorkerId)
            RequireSuccess(await users.AddClaimAsync(user,
                new Claim(AuthClaimTypes.MedicalWorkerId, medicalWorkerId.ToString())));
        audit.Append(AuditEventType.AccountCreated, "Создана учётная запись.",
            new AuditMetadata { Action = "created", TargetId = user.Id, AccountId = user.Id,
                MedicalWorkerId = user.MedicalWorkerId, RevokedRefreshTokenCount = 0 });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new AccountDto(user.Id, name!, request.Role, user.MedicalWorkerId, false,
            user.IsActive, user.CreatedAtUtc, user.UpdatedAtUtc);
    }

    public async Task<ResetAccountPasswordResultDto> ResetPasswordAsync(Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var user = await RequireLockedMedicalAccountAsync(accountId, cancellationToken);
        var password = passwordGenerator.Generate();
        foreach (var validator in users.PasswordValidators)
            RequireSuccess(await validator.ValidateAsync(users, user, password));
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var revoked = await RevokeRefreshSessionsAsync(user.Id, "password-reset", cancellationToken);
        audit.Append(AuditEventType.AccountChanged, "Сброшен пароль учётной записи.",
            Metadata(user, "password-reset", revoked, "password-reset"));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new ResetAccountPasswordResultDto(user.Id, password, revoked);
    }

    public async Task<SetAccountActiveResultDto> SetActiveAsync(Guid accountId, bool isActive,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var user = await RequireLockedMedicalAccountAsync(accountId, cancellationToken);
        var worker = await db.MedicalWorkers.SingleAsync(item => item.Id == user.MedicalWorkerId, cancellationToken);
        if (isActive && worker.IsDeleted)
            throw new DomainValidationException("Нельзя активировать аккаунт удалённого медработника.");
        var revoked = 0;
        if (user.IsActive != isActive)
        {
            user.IsActive = isActive;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            if (!isActive)
                revoked = await RevokeRefreshSessionsAsync(user.Id, "account-deactivated", cancellationToken);
            audit.Append(AuditEventType.AccountChanged,
                isActive ? "Активирована учётная запись." : "Деактивирована учётная запись.",
                Metadata(user, isActive ? "activated" : "deactivated", revoked,
                    isActive ? null : "account-deactivated"));
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new SetAccountActiveResultDto(ToDetails(new AccountDto(user.Id, user.UserName!,
            ApplicationRoles.MedicalWorker, user.MedicalWorkerId, user.LockoutEnd > timeProvider.GetUtcNow(),
            user.IsActive, user.CreatedAtUtc, user.UpdatedAtUtc), worker), revoked);
    }

    private async Task<HospitalUser> RequireLockedMedicalAccountAsync(Guid accountId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {accountId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new EntityNotFoundException("Учётная запись не найдена.");
        if (user.IsAdministrator || user.MedicalWorkerId is null)
            throw new DomainValidationException("Операция доступна только для аккаунта медработника.");
        return user;
    }

    private async Task<int> RevokeRefreshSessionsAsync(Guid accountId, string reason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sessions = await db.RefreshSessions.Where(session => session.UserId == accountId &&
            session.RevokedAtUtc == null && session.ExpiresAtUtc > now).ToArrayAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevokeReason = reason;
        }
        return sessions.Length;
    }

    private static AuditMetadata Metadata(HospitalUser user, string action, int revoked, string? reason) => new()
    {
        Action = action, TargetId = user.Id, AccountId = user.Id, MedicalWorkerId = user.MedicalWorkerId,
        RevokedRefreshTokenCount = revoked, RevokeReason = reason
    };

    private static AccountDetailsDto ToDetails(AccountDto account,
        Persistence.Models.MedicalWorkerDbo? worker) => new(account.Id, account.UserName, account.Role,
        account.MedicalWorkerId, worker is null ? null : $"{worker.LastName} {worker.FirstName}",
        worker is null ? null : AccountMapping.ToContractRole(worker.Role),
        account.IsActive, account.IsLockedOut, account.CreatedAtUtc, account.UpdatedAtUtc);

    public async Task<SessionDto?> GetSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || !user.IsActive || user.UserName is null || user.LockoutEnd > timeProvider.GetUtcNow())
            return null;
        var roles = await (from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == user.Id
            select role.Name).ToArrayAsync(cancellationToken);
        if (roles.Length != 1 || roles[0] is not (ApplicationRoles.Administrator or ApplicationRoles.MedicalWorker) ||
            user.IsAdministrator != (roles[0] == ApplicationRoles.Administrator))
            return null;
        HospitalInformationSystem.Contracts.Security.MedicalWorkerRole? medicalRole = null;
        if (roles[0] == ApplicationRoles.MedicalWorker)
        {
            if (user.MedicalWorkerId is not { } workerId)
                return null;
            var worker = await db.MedicalWorkers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == workerId && !x.IsDeleted, cancellationToken);
            if (worker is null)
                return null;
            medicalRole = worker.Role switch
            {
                MedicalWorkerRole.Doctor => HospitalInformationSystem.Contracts.Security.MedicalWorkerRole.Doctor,
                MedicalWorkerRole.LaboratoryTechnician => HospitalInformationSystem.Contracts.Security.MedicalWorkerRole.LaboratoryTechnician,
                MedicalWorkerRole.Nurse => HospitalInformationSystem.Contracts.Security.MedicalWorkerRole.Nurse,
                MedicalWorkerRole.Other => HospitalInformationSystem.Contracts.Security.MedicalWorkerRole.Other,
                _ => null
            };
            if (medicalRole is null)
                return null;
        }
        else if (user.MedicalWorkerId is not null)
            return null;
        return new SessionDto(user.Id, user.UserName, roles[0]!, user.MedicalWorkerId, medicalRole);
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (result.Succeeded) return;
        if (result.Errors.Any(x => x.Code == nameof(IdentityErrorDescriber.DuplicateUserName)))
            throw new BusinessConflictException("Этот логин уже используется.");
        throw new DomainValidationException(string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
