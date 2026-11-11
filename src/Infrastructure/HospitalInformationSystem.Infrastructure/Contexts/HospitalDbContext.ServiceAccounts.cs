using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Contexts;

public sealed partial class HospitalDbContext
{
    public DbSet<ServiceAccountDbo> ServiceAccounts => Set<ServiceAccountDbo>();
}
