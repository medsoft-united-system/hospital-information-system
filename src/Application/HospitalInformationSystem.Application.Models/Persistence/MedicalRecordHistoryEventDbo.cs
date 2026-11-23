using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class MedicalRecordHistoryEventDbo
{
    public Guid Id { get; set; }
    public Guid MedicalRecordId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public MedicalRecordAuthorKind AuthorKind { get; set; }
    public Guid? PerformedByMedicalWorkerId { get; set; }
    public Guid? PerformedByServiceAccountId { get; set; }
    public string PerformedByNameSnapshot { get; set; } = string.Empty;
    public string PerformedByPositionSnapshot { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}
