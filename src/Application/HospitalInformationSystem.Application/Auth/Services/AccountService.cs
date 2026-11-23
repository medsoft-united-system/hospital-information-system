using HospitalInformationSystem.Application.Abstractions.Identity;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.Transactions;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Security;
using SessionWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;
using WorkerRole = HospitalInformationSystem.Application.Models.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.Application.Auth.Services;

public sealed class AccountService(
    IAccountRepository accounts,
    IMedicalWorkerRepository workers,
    IUnitOfWork unitOfWork,
    ITransactionManager transactions,
    TimeProvider timeProvider,
    IAccountPasswordGenerator passwordGenerator,
    IAuditWriter audit) : IAccountService
{
    public async Task<IReadOnlyList<AccountDetailsDto>> ListDetailsAsync(CancellationToken cancellationToken)
    {
        var listed = await ListAsync(cancellationToken);
        var medicalWorkers = (await workers.ListAsync(includeDeleted: true, cancellationToken))
            .ToDictionary(worker => worker.Id);
        return listed.Select(account => ToDetails(account,
            account.MedicalWorkerId is { } workerId && medicalWorkers.TryGetValue(workerId, out var worker)
                ? worker : null)).ToArray();
    }

    public async Task<CreateWorkerAccountResultDto> CreateForWorkerAsync(Guid medicalWorkerId,
        CreateWorkerAccountRequest request, CancellationToken cancellationToken)
    {
        var password = passwordGenerator.Generate();
        var account = await CreateAsync(new CreateAccountRequest(request.UserName, password,
            ApplicationRoles.MedicalWorker, medicalWorkerId), cancellationToken);
        var worker = await workers.FindAsync(medicalWorkerId, tracking: false, cancellationToken)
            ?? throw new EntityNotFoundException("Медработник не найден.");
        return new CreateWorkerAccountResultDto(ToDetails(account, worker), password);
    }

    public async Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return (await accounts.ListAsync(cancellationToken))
            .SelectMany(account => account.Roles.Select(role => ToDto(account, role, now))).ToArray();
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
        await using var transaction = transactions.HasActiveTransaction
            ? null : await transactions.BeginAsync(cancellationToken);
        if (request.Role == ApplicationRoles.MedicalWorker)
        {
            if (request.MedicalWorkerId is not { } workerId)
                throw new DomainValidationException("Выберите действующего медработника.");
            var worker = await accounts.FindMedicalWorkerForUpdateAsync(workerId, cancellationToken);
            if (worker is null || worker.IsDeleted)
                throw new DomainValidationException("Выберите действующего медработника.");
            if (await accounts.HasMedicalWorkerAccountAsync(workerId, cancellationToken))
                throw new BusinessConflictException("У медработника уже есть учётная запись.");
        }
        if (request.Role == ApplicationRoles.Administrator && await accounts.HasAdministratorAsync(cancellationToken))
            throw new BusinessConflictException("В системе уже существует администратор.");
        var now = timeProvider.GetUtcNow();
        var account = new AccountIdentity(Guid.NewGuid(), name, request.MedicalWorkerId,
            request.Role == ApplicationRoles.Administrator, true, null, now, now, [request.Role]);
        await accounts.CreateAsync(account, request.Password, cancellationToken);
        audit.Append(AuditEventType.AccountCreated, "Создана учётная запись.",
            new AuditMetadata
            {
                Action = "created",
                TargetId = account.Id,
                AccountId = account.Id,
                MedicalWorkerId = account.MedicalWorkerId,
                RevokedRefreshTokenCount = 0
            });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        account = await accounts.FindAsync(account.Id, cancellationToken)
            ?? throw new EntityNotFoundException("Учётная запись не найдена.");
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return ToDto(account, request.Role, now);
    }

    public async Task<ResetAccountPasswordResultDto> ResetPasswordAsync(Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var transaction = transactions.HasActiveTransaction
            ? null : await transactions.BeginAsync(cancellationToken);
        var account = await RequireLockedMedicalAccountAsync(accountId, cancellationToken);
        var password = passwordGenerator.Generate();
        await accounts.ResetPasswordAsync(account.Id, password, cancellationToken);
        var revoked = await accounts.RevokeRefreshSessionsAsync(account.Id, "password-reset",
            timeProvider.GetUtcNow(), cancellationToken);
        audit.Append(AuditEventType.AccountChanged, "Сброшен пароль учётной записи.",
            Metadata(account, "password-reset", revoked, "password-reset"));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new ResetAccountPasswordResultDto(account.Id, password, revoked);
    }

    public async Task<SetAccountActiveResultDto> SetActiveAsync(Guid accountId, bool isActive,
        CancellationToken cancellationToken)
    {
        await using var transaction = transactions.HasActiveTransaction
            ? null : await transactions.BeginAsync(cancellationToken);
        var account = await RequireLockedMedicalAccountAsync(accountId, cancellationToken);
        var worker = await workers.FindAsync(account.MedicalWorkerId!.Value, tracking: false, cancellationToken)
            ?? throw new EntityNotFoundException("Медработник не найден.");
        if (isActive && worker.IsDeleted)
            throw new DomainValidationException("Нельзя активировать аккаунт удалённого медработника.");
        var revoked = 0;
        if (account.IsActive != isActive)
        {
            await accounts.SetActiveAsync(account.Id, isActive, cancellationToken);
            if (!isActive)
                revoked = await accounts.RevokeRefreshSessionsAsync(account.Id, "account-deactivated",
                    timeProvider.GetUtcNow(), cancellationToken);
            audit.Append(AuditEventType.AccountChanged,
                isActive ? "Активирована учётная запись." : "Деактивирована учётная запись.",
                Metadata(account, isActive ? "activated" : "deactivated", revoked,
                    isActive ? null : "account-deactivated"));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            account = await accounts.FindAsync(account.Id, cancellationToken)
                ?? throw new EntityNotFoundException("Учётная запись не найдена.");
        }
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new SetAccountActiveResultDto(ToDetails(ToDto(account, ApplicationRoles.MedicalWorker,
            timeProvider.GetUtcNow()), worker), revoked);
    }

    public async Task<SessionDto?> GetSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(userId, cancellationToken);
        if (account is null || !account.IsActive || account.UserName is null ||
            account.LockoutEnd > timeProvider.GetUtcNow())
            return null;
        var roles = account.Roles;
        if (roles.Count != 1 || roles[0] is not (ApplicationRoles.Administrator or ApplicationRoles.MedicalWorker) ||
            account.IsAdministrator != (roles[0] == ApplicationRoles.Administrator))
            return null;
        SessionWorkerRole? medicalRole = null;
        if (roles[0] == ApplicationRoles.MedicalWorker)
        {
            if (account.MedicalWorkerId is not { } workerId)
                return null;
            var worker = await workers.FindAsync(workerId, tracking: false, cancellationToken);
            if (worker is null || worker.IsDeleted)
                return null;
            medicalRole = worker.Role switch
            {
                WorkerRole.Doctor => SessionWorkerRole.Doctor,
                WorkerRole.LaboratoryTechnician => SessionWorkerRole.LaboratoryTechnician,
                WorkerRole.Nurse => SessionWorkerRole.Nurse,
                WorkerRole.Other => SessionWorkerRole.Other,
                _ => null
            };
            if (medicalRole is null)
                return null;
        }
        else if (account.MedicalWorkerId is not null)
            return null;
        return new SessionDto(account.Id, account.UserName, roles[0], account.MedicalWorkerId, medicalRole);
    }

    private async Task<AccountIdentity> RequireLockedMedicalAccountAsync(Guid accountId,
        CancellationToken cancellationToken)
    {
        var account = await accounts.FindForUpdateAsync(accountId, cancellationToken)
            ?? throw new EntityNotFoundException("Учётная запись не найдена.");
        if (account.IsAdministrator || account.MedicalWorkerId is null)
            throw new DomainValidationException("Операция доступна только для аккаунта медработника.");
        return account;
    }

    private static AuditMetadata Metadata(AccountIdentity account, string action, int revoked, string? reason) => new()
    {
        Action = action,
        TargetId = account.Id,
        AccountId = account.Id,
        MedicalWorkerId = account.MedicalWorkerId,
        RevokedRefreshTokenCount = revoked,
        RevokeReason = reason
    };

    private static AccountDto ToDto(AccountIdentity account, string role, DateTimeOffset now) =>
        new(account.Id, account.UserName!, role, account.MedicalWorkerId, account.LockoutEnd > now,
            account.IsActive, account.CreatedAtUtc, account.UpdatedAtUtc);

    private static AccountDetailsDto ToDetails(AccountDto account, MedicalWorker? worker) =>
        new(account.Id, account.UserName, account.Role, account.MedicalWorkerId,
            worker is null ? null : $"{worker.LastName} {worker.FirstName}",
            worker is null ? null : AccountMapping.ToContractRole(worker.Role), account.IsActive, account.IsLockedOut,
            account.CreatedAtUtc, account.UpdatedAtUtc);
}
