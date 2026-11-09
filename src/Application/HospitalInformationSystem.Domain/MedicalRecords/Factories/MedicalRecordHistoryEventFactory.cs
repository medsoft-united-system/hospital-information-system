using System.Text.Json;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.MedicalWorkers;

namespace HospitalInformationSystem.Domain.MedicalRecords.Factories;

public static class MedicalRecordHistoryEventFactory
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static MedicalRecordHistoryEvent Create<TPayload>(
        Guid medicalRecordId, long version, MedicalRecordEventType type,
        Guid? entityId, TPayload payload, MedicalWorker author, DateTimeOffset at)
    {
        if (type == MedicalRecordEventType.LaboratoryAnalysisResultRecorded)
            throw new DomainValidationException("Результат анализа регистрируется от имени сервисного аккаунта.");
        return new MedicalRecordHistoryEvent
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, Version = version,
            OccurredAtUtc = at.ToUniversalTime(), PerformedByMedicalWorkerId = author.Id,
            PerformedByNameSnapshot = $"{author.LastName} {author.FirstName}",
            PerformedByPositionSnapshot = author.Position, EventType = type, EntityId = entityId,
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions)
        };
    }

    public static MedicalRecordHistoryEvent CreateForService<TPayload>(Guid medicalRecordId, long version,
        MedicalRecordEventType type, Guid? entityId, TPayload payload, Guid serviceAccountId,
        string serviceAccountName, DateTimeOffset at)
    {
        if (type != MedicalRecordEventType.LaboratoryAnalysisResultRecorded || serviceAccountId == Guid.Empty)
            throw new DomainValidationException("Сервисный автор разрешён только для регистрации результата анализа.");
        var name = serviceAccountName?.Trim();
        if (name is null || name.Contains('\0') || name.EnumerateRunes().Count() is < 3 or > 64)
            throw new DomainValidationException("Имя сервисного автора должно содержать от 3 до 64 символов.");
        return new MedicalRecordHistoryEvent
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, Version = version,
            OccurredAtUtc = at.ToUniversalTime(), AuthorKind = MedicalRecordAuthorKind.ServiceAccount,
            PerformedByServiceAccountId = serviceAccountId, PerformedByNameSnapshot = name,
            PerformedByPositionSnapshot = "LIS", EventType = type, EntityId = entityId,
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions)
        };
    }
}
