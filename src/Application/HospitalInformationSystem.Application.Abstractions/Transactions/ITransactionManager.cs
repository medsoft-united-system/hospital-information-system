namespace HospitalInformationSystem.Application.Abstractions.Transactions;

public interface ITransactionManager
{
    bool HasActiveTransaction { get; }
    Task<IApplicationTransaction> BeginAsync(CancellationToken cancellationToken);
    void DiscardChanges();
}
