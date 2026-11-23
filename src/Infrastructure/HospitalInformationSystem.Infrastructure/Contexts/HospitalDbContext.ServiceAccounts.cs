using HospitalInformationSystem.Application.Models.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Contexts;

public sealed partial class HospitalDbContext
{
    public DbSet<ServiceAccountDbo> ServiceAccounts => Set<ServiceAccountDbo>();
    public DbSet<ServiceRefreshSessionDbo> ServiceRefreshSessions => Set<ServiceRefreshSessionDbo>();
}
