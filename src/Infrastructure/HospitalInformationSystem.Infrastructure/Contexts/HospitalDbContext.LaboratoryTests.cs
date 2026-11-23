using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Contexts;

public sealed partial class HospitalDbContext
{
    public DbSet<LaboratoryTestDefinitionDbo> LaboratoryTestDefinitions => Set<LaboratoryTestDefinitionDbo>();
}
