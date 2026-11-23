using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

public sealed class ServiceAccountRepository(HospitalDbContext db) : IServiceAccountRepository
{
    public async Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await PublicAccounts().OrderBy(account => account.Name).ThenBy(account => account.Id)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToDto).ToArray();
    }

    public async Task<ServiceAccountDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await PublicAccounts().SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public async Task<(ServiceAccount Account, uint Version)?> GetForUpdateAsync(Guid id,
        CancellationToken cancellationToken)
    {
        var row = await db.ServiceAccounts.FromSqlInterpolated(
                $"SELECT *, xmin FROM service_accounts WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.ToDomain(), db.Entry(row).Property<uint>("xmin").CurrentValue);
    }

    public Task<bool> NameExistsAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken) =>
        db.ServiceAccounts.AsNoTracking().AnyAsync(row => row.NormalizedName == normalizedName &&
            (!exceptId.HasValue || row.Id != exceptId.Value), cancellationToken);

    public void Add(ServiceAccount account) => db.ServiceAccounts.Add(account.ToDbo());

    public void Update(ServiceAccount account) =>
        account.UpdateDbo(db.ServiceAccounts.Local.Single(row => row.Id == account.Id));

    public ServiceAccountDto Describe(ServiceAccount account)
    {
        var row = db.ServiceAccounts.Local.Single(item => item.Id == account.Id);
        return ToDto(ServiceAccountDtoMapper.ToReadRow(row, db.Entry(row).Property<uint>("xmin").CurrentValue));
    }

    private IQueryable<ServiceAccountReadRow> PublicAccounts() => db.ServiceAccounts.AsNoTracking()
        .Select(row => new ServiceAccountReadRow
        {
            Id = row.Id, Name = row.Name, Role = row.Role, IsActive = row.IsActive,
            CreatedAtUtc = row.CreatedAtUtc, UpdatedAtUtc = row.UpdatedAtUtc,
            Version = EF.Property<uint>(row, "xmin")
        });

    private static ServiceAccountDto ToDto(ServiceAccountReadRow row) =>
        ServiceAccountDtoMapper.ToDto(row, ServiceApiKeyGenerator.Prefix(row.Id));
}
