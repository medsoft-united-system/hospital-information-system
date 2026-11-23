namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed class MedicalRecordHistoryEvent
{
    public Guid Id { get; init; }
    public Guid MedicalRecordId { get; init; }
    public long Version { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public MedicalRecordAuthorKind AuthorKind { get; init; } = MedicalRecordAuthorKind.MedicalWorker;
    public Guid? PerformedByMedicalWorkerId { get; init; }
    public Guid? PerformedByServiceAccountId { get; init; }
    public required string PerformedByNameSnapshot { get; init; }
    public required string PerformedByPositionSnapshot { get; init; }
    public MedicalRecordEventType EventType { get; init; }
    public Guid? EntityId { get; init; }
    public required string PayloadJson { get; init; }
}
