using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.MedicalRecords.Snapshot;

public sealed class MedicalRecordSnapshotBuilder(IMedicalRecordRepository repository) : IMedicalRecordSnapshotBuilder
{
    public async Task<MedicalRecordSnapshotDto> BuildAsync(
        Guid medicalRecordId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var record = await repository.FindAsync(medicalRecordId, tracking: false, cancellationToken)
                     ?? throw new EntityNotFoundException("Медицинская карта не найдена.");
        return MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, at);
    }
}
