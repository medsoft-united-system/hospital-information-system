using HospitalInformationSystem.Application.Abstractions.Transactions;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HospitalInformationSystem.Infrastructure.Persistence;

public sealed class EfTransactionManager(HospitalDbContext db) : ITransactionManager
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<IApplicationTransaction> BeginAsync(CancellationToken cancellationToken) =>
        HasActiveTransaction ? new Transaction(null) :
            new Transaction(await db.Database.BeginTransactionAsync(cancellationToken));

    public void DiscardChanges() => db.ChangeTracker.Clear();

    private sealed class Transaction(IDbContextTransaction? transaction) : IApplicationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            transaction?.RollbackAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
