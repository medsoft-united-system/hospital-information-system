using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Domain.MedicalRecords.Factories;
using HospitalInformationSystem.Domain.MedicalWorkers;

namespace HospitalInformationSystem.Domain.MedicalRecords.Services;

public static partial class MedicalRecordService
{
    public static IReadOnlyList<LaboratoryAnalysisRequest> CreateLaboratoryAnalysisRequests(MedicalRecord record,
        IReadOnlyList<LaboratoryTestDefinition> definitions, string? comment, MedicalWorker author,
        DateTimeOffset now)
    {
        ValidateAuthor(author);
        if (author.Role != MedicalWorkerRole.Doctor)
            throw new DomainValidationException("Назначать лабораторные анализы может только врач.");
        if (definitions is null || definitions.Count is < 1 or > LaboratoryAnalysisRules.MaxBatchSize ||
            definitions.Any(x => x is null) || definitions.Select(x => x.Id).Distinct().Count() != definitions.Count)
            throw new DomainValidationException("Выберите от 1 до 100 разных лабораторных анализов.");
        var normalizedComment = LaboratoryAnalysisRules.NormalizeComment(comment);
        var at = LaboratoryEventTime(record, now);
        var requests = definitions.Select(definition => LaboratoryAnalysisRequestFactory.Create(record.Id,
            record.PatientId, definition, normalizedComment, author.Id, at)).ToArray();
        var events = requests.Select((request, index) => MedicalRecordHistoryEventFactory.Create(record.Id,
            checked(record.Version + index + 1), MedicalRecordEventType.LaboratoryAnalysisRequested, request.Id,
            new LaboratoryAnalysisRequestedPayload(request.Id, request.MedicalRecordId, request.PatientId,
                request.RequestedByMedicalWorkerId, request.LaboratoryTestDefinitionId, request.LoincCodeSnapshot,
                request.DisplayNameSnapshot, request.Comment, request.AuthoredAtUtc), author, at)).ToArray();

        record.LaboratoryAnalysisRequests = record.LaboratoryAnalysisRequests.Concat(requests).ToArray();
        record.HistoryEvents = record.HistoryEvents.Concat(events).ToArray();
        record.Version = events[^1].Version;
        return Array.AsReadOnly(requests);
    }

    public static LaboratoryAnalysisResult RecordLaboratoryAnalysisResult(MedicalRecord record, Guid requestId,
        Guid patientId, string loincCode, string value, DateTimeOffset issuedAtUtc, Guid serviceAccountId,
        string serviceAccountName, DateTimeOffset now)
    {
        var request = record.LaboratoryAnalysisRequests.SingleOrDefault(x => x.Id == requestId)
                      ?? throw new DomainEntityNotFoundException("Назначение анализа в этой медицинской карте не найдено.");
        if (request.MedicalRecordId != record.Id || patientId != record.PatientId || request.PatientId != patientId ||
            request.LoincCodeSnapshot != loincCode?.Trim())
            throw new DomainValidationException("Пациент или код LOINC не соответствуют назначенному анализу.");
        if (request.Status != LaboratoryAnalysisRequestStatus.Active || request.Result is not null)
            throw new BusinessRuleConflictException("Результат этого анализа уже зарегистрирован.");
        var at = LaboratoryEventTime(record, now);
        var result = LaboratoryAnalysisResultFactory.Create(request.Id, patientId, value, issuedAtUtc, at,
            serviceAccountId);
        var historyEvent = MedicalRecordHistoryEventFactory.CreateForService(record.Id, checked(record.Version + 1),
            MedicalRecordEventType.LaboratoryAnalysisResultRecorded, result.Id,
            new LaboratoryAnalysisResultRecordedPayload(request.Id, result.Id, patientId, request.LoincCodeSnapshot,
                result.Value, result.IssuedAtUtc, result.ReceivedAtUtc, result.CreatedByServiceAccountId),
            serviceAccountId, serviceAccountName, at);

        request.Result = result;
        request.Status = LaboratoryAnalysisRequestStatus.Completed;
        record.HistoryEvents = record.HistoryEvents.Append(historyEvent).ToArray();
        record.Version = historyEvent.Version;
        return result;
    }

    private static DateTimeOffset LaboratoryEventTime(MedicalRecord record, DateTimeOffset now)
    {
        var requested = now.ToUniversalTime();
        var at = EventTime(record, requested.AddTicks(-(requested.Ticks % TimeSpan.TicksPerMicrosecond)));
        var remainder = at.Ticks % TimeSpan.TicksPerMicrosecond;
        return remainder == 0 ? at : at.AddTicks(TimeSpan.TicksPerMicrosecond - remainder);
    }
}
