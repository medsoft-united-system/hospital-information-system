using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Domain.ServiceAccounts.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using DomainService = HospitalInformationSystem.Domain.ServiceAccounts.Services.ServiceAccountService;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

public sealed class ServiceAccountService(HospitalDbContext db, TimeProvider timeProvider,
    IServiceApiKeyGenerator keys, IServiceSessionRevoker sessions, IAuditWriter audit) : IServiceAccountService
{
    public async Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await PublicAccounts().OrderBy(account => account.Name).ThenBy(account => account.Id)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToDto).ToArray();
    }

    public async Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireId(id);
        var row = await PublicAccounts().SingleOrDefaultAsync(account => account.Id == id, cancellationToken)
                  ?? throw new EntityNotFoundException("Сервисный аккаунт не найден.");
        return ToDto(row);
    }

    public async Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        var name = ServiceAccountRules.NormalizeName(request.Name);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await RequireUniqueNameAsync(name, null, cancellationToken);
        var id = Guid.NewGuid();
        var material = keys.Generate(id);
        var account = ServiceAccountFactory.Create(id, name, material.Hash, timeProvider.GetUtcNow());
        var row = account.ToDbo();
        db.ServiceAccounts.Add(row);
        audit.Append(AuditEventType.AccountCreated, "Создан сервисный аккаунт LIS.",
            Metadata(id, "service-account-created", 0));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountKeyResultDto(ToDto(row), material.ApiKey, 0);
    }

    public async Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        var name = ServiceAccountRules.NormalizeName(request.Name);
        RequireVersion(request.Version);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var row = await RequireLockedAsync(id, request.Version, cancellationToken);
        await RequireUniqueNameAsync(name, id, cancellationToken);
        var account = row.ToDomain();
        if (DomainService.Rename(account, name, timeProvider.GetUtcNow()))
        {
            account.UpdateDbo(row);
            audit.Append(AuditEventType.AccountChanged, "Изменено имя сервисного аккаунта LIS.",
                Metadata(id, "service-account-renamed", 0));
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id,
        SetServiceAccountActiveRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        RequireVersion(request.Version);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var row = await RequireLockedAsync(id, request.Version, cancellationToken);
        var account = row.ToDomain();
        var revoked = 0;
        var now = timeProvider.GetUtcNow();
        if (DomainService.SetActive(account, request.IsActive, now))
        {
            account.UpdateDbo(row);
            if (!request.IsActive)
                revoked = await sessions.RevokeAllAsync(id, now, "service-account-deactivated", cancellationToken);
            audit.Append(AuditEventType.AccountChanged, request.IsActive ? "Активирован сервисный аккаунт LIS." :
                    "Отключён сервисный аккаунт LIS.",
                Metadata(id, request.IsActive ? "service-account-activated" : "service-account-deactivated", revoked,
                    request.IsActive ? null : "service-account-deactivated"));
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountActivityResultDto(ToDto(row), revoked);
    }

    public async Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw InvalidRequest();
        RequireVersion(request.Version);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var row = await RequireLockedAsync(id, request.Version, cancellationToken);
        var account = row.ToDomain();
        var material = keys.Generate(id);
        var now = timeProvider.GetUtcNow();
        DomainService.RotateKey(account, material.Hash, now);
        account.UpdateDbo(row);
        var revoked = await sessions.RevokeAllAsync(id, now, "service-key-rotated", cancellationToken);
        audit.Append(AuditEventType.AccountChanged, "Заменён API key сервисного аккаунта LIS.",
            Metadata(id, "service-key-rotated", revoked, "service-key-rotated"));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new ServiceAccountKeyResultDto(ToDto(row), material.ApiKey, revoked);
    }

    private IQueryable<ServiceAccountReadRow> PublicAccounts() => db.ServiceAccounts.AsNoTracking()
        .Select(row => new ServiceAccountReadRow
        {
            Id = row.Id, Name = row.Name, Role = row.Role, IsActive = row.IsActive,
            CreatedAtUtc = row.CreatedAtUtc, UpdatedAtUtc = row.UpdatedAtUtc,
            Version = EF.Property<uint>(row, "xmin")
        });

    private async Task<ServiceAccountDbo> RequireLockedAsync(Guid id, uint version,
        CancellationToken cancellationToken)
    {
        RequireId(id);
        RequireVersion(version);
        var row = await db.ServiceAccounts.FromSqlInterpolated(
                $"SELECT *, xmin FROM service_accounts WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
                  ?? throw new EntityNotFoundException("Сервисный аккаунт не найден.");
        if (db.Entry(row).Property<uint>("xmin").CurrentValue != version)
            throw new BusinessConflictException("Сервисный аккаунт уже изменён. Обновите список перед повторением операции.");
        return row;
    }

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var normalized = name.ToUpperInvariant();
        if (await db.ServiceAccounts.AsNoTracking().AnyAsync(row => row.NormalizedName == normalized &&
                (!exceptId.HasValue || row.Id != exceptId.Value), cancellationToken))
            throw new BusinessConflictException("Сервисный аккаунт с таким именем уже существует.");
    }

    private ServiceAccountDto ToDto(ServiceAccountDbo row) =>
        ToDto(ServiceAccountDtoMapper.ToReadRow(row, db.Entry(row).Property<uint>("xmin").CurrentValue));

    private static ServiceAccountDto ToDto(ServiceAccountReadRow row) =>
        ServiceAccountDtoMapper.ToDto(row, ServiceApiKeyGenerator.Prefix(row.Id));

    private static AuditMetadata Metadata(Guid id, string action, int revoked, string? reason = null) => new()
    {
        Action = action, TargetId = id, ServiceAccountId = id,
        RevokedServiceSessionCount = revoked, RevokeReason = reason
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
