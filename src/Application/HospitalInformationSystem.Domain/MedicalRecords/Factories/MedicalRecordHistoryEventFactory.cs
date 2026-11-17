using System.Text.Json;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.ServiceAccounts;

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
        if (type is not MedicalRecordEventType.LaboratoryAnalysisResultRecorded and
            not MedicalRecordEventType.MedicalRecordCreated and not MedicalRecordEventType.DiagnosisAdded)
            throw new DomainValidationException("Недопустимое событие сервисного автора.");
        var name = ValidateServiceAuthor(serviceAccountId, serviceAccountName);
        return new MedicalRecordHistoryEvent
        {
            Id = Guid.NewGuid(), MedicalRecordId = medicalRecordId, Version = version,
            OccurredAtUtc = at.ToUniversalTime(), AuthorKind = MedicalRecordAuthorKind.ServiceAccount,
            PerformedByServiceAccountId = serviceAccountId, PerformedByNameSnapshot = name,
            PerformedByPositionSnapshot = type == MedicalRecordEventType.LaboratoryAnalysisResultRecorded
                ? "LIS" : "HL7 Import", EventType = type, EntityId = entityId,
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions)
        };
    }

    internal static string ValidateServiceAuthor(Guid serviceAccountId, string serviceAccountName)
    {
        if (serviceAccountId == Guid.Empty)
            throw new DomainValidationException("Не указан сервисный автор.");
        return ServiceAccountRules.NormalizeName(serviceAccountName);
    }
}
