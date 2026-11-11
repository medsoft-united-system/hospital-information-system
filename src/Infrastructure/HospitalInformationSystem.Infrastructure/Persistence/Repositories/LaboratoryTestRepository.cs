using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Mappings;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Persistence.Repositories;

public sealed class LaboratoryTestRepository(HospitalDbContext db) : ILaboratoryTestRepository
{
    public async Task<IReadOnlyList<LaboratoryTestDefinition>> ListAsync(bool activeOnly,
        CancellationToken cancellationToken)
    {
        var rows = await db.LaboratoryTestDefinitions.AsNoTracking()
            .Where(x => !activeOnly || x.IsActive)
            .OrderBy(x => x.LoincCode).ThenBy(x => x.Id)
            .Select(x => new { Definition = x, Version = EF.Property<uint>(x, "xmin") })
            .ToListAsync(cancellationToken);
        return rows.Select(x => x.Definition.ToDomain(x.Version)).ToArray();
    }

    public async Task<LaboratoryTestDefinition?> FindAsync(Guid id, bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? db.LaboratoryTestDefinitions.AsTracking() : db.LaboratoryTestDefinitions.AsNoTracking();
        var row = await query.Where(x => x.Id == id)
            .Select(x => new { Definition = x, Version = EF.Property<uint>(x, "xmin") })
            .SingleOrDefaultAsync(cancellationToken);
        return row?.Definition.ToDomain(row.Version);
    }

    public Task<bool> ExistsCodeAsync(string code, Guid? excludingId, CancellationToken cancellationToken) =>
        db.LaboratoryTestDefinitions.AsNoTracking().AnyAsync(
            x => (!excludingId.HasValue || x.Id != excludingId.Value) && x.LoincCode == code,
            cancellationToken);

    public void Add(LaboratoryTestDefinition entity) => db.LaboratoryTestDefinitions.Add(entity.ToDbo());

    public async Task UpdateAsync(LaboratoryTestDefinition entity, CancellationToken cancellationToken)
    {
        var row = await db.LaboratoryTestDefinitions.FindAsync([entity.Id], cancellationToken)
                  ?? throw new EntityNotFoundException("Лабораторный анализ не найден.");
        var version = db.Entry(row).Property<uint>("xmin");
        if (version.CurrentValue != entity.Version)
            throw new BusinessConflictException("Анализ уже изменён. Обновите справочник и повторите действие.");
        version.OriginalValue = entity.Version;
        entity.UpdateDbo(row);
    }
}
