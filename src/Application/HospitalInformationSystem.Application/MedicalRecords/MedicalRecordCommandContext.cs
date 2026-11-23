using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Abstractions.UnitOfWork;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.MedicalWorkers;

namespace HospitalInformationSystem.Application.MedicalRecords;

public sealed class MedicalRecordCommandContext(
    IMedicalRecordRepository records,
    IMedicalWorkerRepository workers,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<MedicalWorker> RequireAuthorAsync(Guid id, CancellationToken cancellationToken)
    {
        var author = await workers.FindAsync(id, tracking: false, cancellationToken);
        if (author is null || author.IsDeleted)
            throw new DomainValidationException("Учётная запись должна быть связана с действующим медработником.");
        return author;
    }

    public async Task<MedicalRecord> RequireRecordAsync(Guid id, CancellationToken cancellationToken) =>
        await records.FindAsync(id, tracking: true, cancellationToken)
        ?? throw new EntityNotFoundException("ЭМК не найдена.");

    public async Task SaveNewAsync(MedicalRecord record, CancellationToken cancellationToken)
    {
        records.Add(record);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(MedicalRecord record, CancellationToken cancellationToken)
    {
        await records.UpdateAsync(record, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
