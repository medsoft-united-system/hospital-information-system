using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.Transactions;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;
using DomainService = HospitalInformationSystem.Application.ServiceAccounts.Services.ServiceAccountDomainService;

namespace HospitalInformationSystem.Application.ServiceAccounts.Services;

public sealed class ServiceAccountService(IServiceAccountRepository repository, IUnitOfWork unitOfWork,
    ITransactionManager transactions, TimeProvider timeProvider,
    IServiceApiKeyGenerator keys, IServiceSessionRevoker sessions, IAuditWriter audit) : IServiceAccountService
{
    public async Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        return await repository.ListAsync(cancellationToken);
    }

    public async Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireId(id);
        return await repository.GetAsync(id, cancellationToken)
            ?? throw new EntityNotFoundException("Сервисный аккаунт не найден.");
    }

    public async Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        var name = ServiceAccountRules.NormalizeName(request.Name);
        var role = ParseRole(request.Role);
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        await RequireUniqueNameAsync(name, null, cancellationToken);
        var id = Guid.NewGuid();
        var material = keys.Generate(id);
        var account = ServiceAccountFactory.Create(id, name, material.Hash, timeProvider.GetUtcNow(), role);
        repository.Add(account);
        audit.Append(AuditEventType.AccountCreated, $"Создан сервисный аккаунт {request.Role}.",
            Metadata(id, "service-account-created", 0));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountKeyResultDto(repository.Describe(account), material.ApiKey, 0);
    }

    public async Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        var name = ServiceAccountRules.NormalizeName(request.Name);
        RequireVersion(request.Version);
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        var account = await RequireLockedAsync(id, request.Version, cancellationToken);
        await RequireUniqueNameAsync(name, id, cancellationToken);
        if (DomainService.Rename(account, name, timeProvider.GetUtcNow()))
        {
            repository.Update(account);
            var role = RoleName(account.Role);
            audit.Append(AuditEventType.AccountChanged, $"Изменено имя сервисного аккаунта {role}.",
                Metadata(id, "service-account-renamed", 0));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return repository.Describe(account);
    }

    public async Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id,
        SetServiceAccountActiveRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        RequireVersion(request.Version);
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        var account = await RequireLockedAsync(id, request.Version, cancellationToken);
        var revoked = 0;
        var now = timeProvider.GetUtcNow();
        if (DomainService.SetActive(account, request.IsActive, now))
        {
            repository.Update(account);
            if (!request.IsActive)
                revoked = await sessions.RevokeAllAsync(id, now, "service-account-deactivated", cancellationToken);
            var role = RoleName(account.Role);
            audit.Append(AuditEventType.AccountChanged, request.IsActive ? $"Активирован сервисный аккаунт {role}." :
                    $"Отключён сервисный аккаунт {role}.",
                Metadata(id, request.IsActive ? "service-account-activated" : "service-account-deactivated", revoked,
                    request.IsActive ? null : "service-account-deactivated"));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountActivityResultDto(repository.Describe(account), revoked);
    }

    public async Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        RequireVersion(request.Version);
        await using var transaction = await transactions.BeginAsync(cancellationToken);
        var account = await RequireLockedAsync(id, request.Version, cancellationToken);
        var material = keys.Generate(id);
        var now = timeProvider.GetUtcNow();
        DomainService.RotateKey(account, material.Hash, now);
        repository.Update(account);
        var revoked = await sessions.RevokeAllAsync(id, now, "service-key-rotated", cancellationToken);
        var role = RoleName(account.Role);
        audit.Append(AuditEventType.AccountChanged, $"Заменён API key сервисного аккаунта {role}.",
            Metadata(id, "service-key-rotated", revoked, "service-key-rotated"));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountKeyResultDto(repository.Describe(account), material.ApiKey, revoked);
    }

    private async Task<ServiceAccount> RequireLockedAsync(Guid id, uint version,
        CancellationToken cancellationToken)
    {
        RequireId(id);
        RequireVersion(version);
        var state = await repository.GetForUpdateAsync(id, cancellationToken)
            ?? throw new EntityNotFoundException("Сервисный аккаунт не найден.");
        if (state.Version != version)
            throw new BusinessConflictException("Сервисный аккаунт уже изменён. Обновите список перед повторением операции.");
        return state.Account;
    }

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await repository.NameExistsAsync(name.ToUpperInvariant(), exceptId, cancellationToken))
            throw new BusinessConflictException("Сервисный аккаунт с таким именем уже существует.");
    }

    private static string RoleName(ServiceAccountRole role) => role switch
    {
        ServiceAccountRole.Lis => ServiceAccountRoles.Lis,
        ServiceAccountRole.Hl7Import => ServiceAccountRoles.Hl7Import,
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта.")
    };

    private static AuditMetadata Metadata(Guid id, string action, int revoked, string? reason = null) => new()
    {
        Action = action,
        TargetId = id,
        ServiceAccountId = id,
        RevokedServiceSessionCount = revoked,
        RevokeReason = reason
    };

    private static ServiceAccountRole ParseRole(string? role) => role switch
    {
        ServiceAccountRoles.Lis => ServiceAccountRole.Lis,
        ServiceAccountRoles.Hl7Import => ServiceAccountRole.Hl7Import,
        _ => throw new DomainValidationException("Неизвестная роль сервисного аккаунта.")
    };

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Укажите идентификатор сервисного аккаунта.");
    }

    private static void RequireVersion(uint version)
    {
        if (version == 0) throw new DomainValidationException("Укажите текущую версию сервисного аккаунта.");
    }

    private static DomainValidationException InvalidRequest() => new("Укажите параметры сервисного аккаунта.");
}
