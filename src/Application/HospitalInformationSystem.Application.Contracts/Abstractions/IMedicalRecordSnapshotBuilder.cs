using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Contracts.Abstractions;

public interface IMedicalRecordSnapshotBuilder
{
    Task<MedicalRecordSnapshotDto> BuildAsync(Guid medicalRecordId, DateTimeOffset at, CancellationToken cancellationToken);
}
