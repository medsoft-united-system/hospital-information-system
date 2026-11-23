namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record MedicalRecordHistoryEventDto(
    long Version,
    DateTimeOffset OccurredAtUtc,
    Guid? PerformedByMedicalWorkerId,
    string PerformedByName,
    string PerformedByPosition,
    string EventType,
    Guid? EntityId,
    string PayloadJson)
{
    public MedicalRecordAuthorKind AuthorKind { get; init; } = MedicalRecordAuthorKind.MedicalWorker;
    public Guid? PerformedByServiceAccountId { get; init; }
}
