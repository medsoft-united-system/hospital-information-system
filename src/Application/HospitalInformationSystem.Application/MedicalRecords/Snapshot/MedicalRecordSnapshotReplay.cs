using System.Text.Json;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using ContractAnalysisStatus = HospitalInformationSystem.Application.Models.MedicalRecords.LaboratoryAnalysisRequestStatus;
using DomainAuthorKind = HospitalInformationSystem.Application.Models.MedicalRecords.MedicalRecordAuthorKind;

namespace HospitalInformationSystem.Application.MedicalRecords.Snapshot;

public static class MedicalRecordSnapshotReplay
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static MedicalRecordSnapshotDto Build(
        MedicalRecord record, IEnumerable<MedicalRecordHistoryEvent> historyEvents, DateTimeOffset at)
    {
        at = at.ToUniversalTime();
        if (at < record.CreatedAtUtc)
            throw new EntityNotFoundException("На выбранный момент медицинская карта ещё не существовала.");

        var diagnoses = new Dictionary<Guid, MedicalRecordDiagnosisDto>();
        var prescriptions = new Dictionary<Guid, PrescriptionDto>();
        var laboratoryRequests = new Dictionary<Guid, LaboratoryAnalysisRequestDto>();
        var laboratoryResultIds = new HashSet<Guid>();
        var diagnosisIds = new HashSet<Guid>();
        var prescriptionIds = new HashSet<Guid>();
        var cancelledPrescriptionIds = new HashSet<Guid>();
        var replacedPrescriptionIds = new HashSet<Guid>();
        var completeHistory = historyEvents.OrderBy(x => x.Version).ToArray();
        MedicalRecordSnapshotDto? snapshot = null;
        var version = 0L;
        var previousTime = record.CreatedAtUtc;

        foreach (var item in completeHistory)
        {
            if (item.OccurredAtUtc > at && snapshot is null)
                snapshot = Capture();
            Require(item.MedicalRecordId == record.Id, "событие принадлежит другой карте");
            Require(item.Version == version + 1, "нарушена последовательность версий");
            Require(item.OccurredAtUtc >= previousTime, "время события меньше времени предыдущего");
            Require(version != 0 || item.EventType == MedicalRecordEventType.MedicalRecordCreated,
                "первое событие должно создавать карту");
            var humanAuthor = item.AuthorKind == DomainAuthorKind.ServiceAccount
                ? (Guid?)null : RequireHumanAuthor(item);
            var serviceAuthor = humanAuthor is null ? RequireServiceAuthor(item) : (Guid?)null;

            switch (item.EventType)
            {
                case MedicalRecordEventType.MedicalRecordCreated:
                    {
                        var payload = Read<MedicalRecordCreatedPayload>(item);
                        Require(
                            version == 0 && payload.PatientId == record.PatientId &&
                            item.OccurredAtUtc == record.CreatedAtUtc,
                            "некорректное событие создания карты");
                        RequireEntityId(item, record.Id);
                        break;
                    }
                case MedicalRecordEventType.DiagnosisAdded:
                    {
                        var payload = Read<DiagnosisAddedPayload>(item);
                        RequireEntityId(item, payload.MedicalRecordDiagnosisId);
                        Require(payload.DiagnosisDefinitionId != Guid.Empty &&
                                MedicalRecordDiagnosisRules.IsValidCode(payload.Code) &&
                                MedicalRecordDiagnosisRules.IsValidName(payload.Name),
                            "неполные данные добавленного диагноза");
                        Require(MedicalRecordDiagnosisRules.IsValidClinicalTime(payload.DiagnosedAtUtc,
                                payload.DiagnosedAtOffsetMinutes),
                            "некорректное клиническое время диагноза");
                        Require((payload.CodingSystem is null || MedicalRecordDiagnosisRules.IsValidMetadata(payload.CodingSystem)) &&
                                (payload.DiagnosisType is null || MedicalRecordDiagnosisRules.IsValidMetadata(payload.DiagnosisType)),
                            "некорректная система кодирования или тип диагноза");
                        RequireDiagnosisAuthor(item, payload);
                        Require(serviceAuthor is null || (payload.DiagnosedAtUtc is not null &&
                                payload.CodingSystem is not null && payload.DiagnosisType is not null),
                            "неполные данные импортированного диагноза");
                        Require(diagnosisIds.Add(payload.MedicalRecordDiagnosisId),
                            "повторное добавление той же записи диагноза");
                        diagnoses.Add(payload.MedicalRecordDiagnosisId, new MedicalRecordDiagnosisDto(
                            payload.MedicalRecordDiagnosisId, payload.Code, payload.Name, item.OccurredAtUtc,
                            humanAuthor, serviceAuthor, payload.DiagnosedAtUtc, payload.DiagnosedAtOffsetMinutes,
                            payload.CodingSystem, payload.DiagnosisType));
                        break;
                    }
                case MedicalRecordEventType.DiagnosisRemoved:
                    {
                        var payload = Read<DiagnosisRemovedPayload>(item);
                        RequireEntityId(item, payload.MedicalRecordDiagnosisId);
                        Require(diagnoses.TryGetValue(payload.MedicalRecordDiagnosisId, out var diagnosis),
                            "удаление отсутствующего диагноза");
                        Require(diagnosis!.Code == payload.Code && diagnosis.Name == payload.Name,
                            "изменён исторический диагноз");
                        diagnoses.Remove(payload.MedicalRecordDiagnosisId);
                        break;
                    }
                case MedicalRecordEventType.PrescriptionAdded:
                    {
                        var payload = Read<PrescriptionAddedPayload>(item);
                        RequireEntityId(item, payload.PrescriptionId);
                        Require(!string.IsNullOrWhiteSpace(payload.Text), "отсутствует текст назначения");
                        Require(prescriptionIds.Add(payload.PrescriptionId), "повторное добавление того же назначения");
                        Require(
                            payload.ReplacesPrescriptionId is null ||
                            (cancelledPrescriptionIds.Contains(payload.ReplacesPrescriptionId.Value) &&
                             replacedPrescriptionIds.Add(payload.ReplacesPrescriptionId.Value)),
                            "заменяемое назначение не было отменено или уже заменено");
                        prescriptions.Add(payload.PrescriptionId, new PrescriptionDto(payload.PrescriptionId, payload.Text,
                            item.OccurredAtUtc, humanAuthor!.Value, payload.ReplacesPrescriptionId));
                        break;
                    }
                case MedicalRecordEventType.PrescriptionCancelled:
                    {
                        var payload = Read<PrescriptionCancelledPayload>(item);
                        RequireEntityId(item, payload.PrescriptionId);
                        Require(prescriptions.TryGetValue(payload.PrescriptionId, out var prescription),
                            "отмена отсутствующего назначения");
                        Require(prescription!.Text == payload.Text, "изменён исторический текст назначения");
                        prescriptions.Remove(payload.PrescriptionId);
                        cancelledPrescriptionIds.Add(payload.PrescriptionId);
                        break;
                    }
                case MedicalRecordEventType.LaboratoryAnalysisRequested:
                    {
                        var payload = Read<LaboratoryAnalysisRequestedPayload>(item);
                        RequireEntityId(item, payload.LaboratoryAnalysisRequestId);
                        Require(payload.MedicalRecordId == record.Id && payload.PatientId == record.PatientId,
                            "назначение анализа относится к другой карте или пациенту");
                        Require(payload.RequestedByMedicalWorkerId == humanAuthor &&
                                payload.LaboratoryTestDefinitionId != Guid.Empty,
                            "неверный автор или отсутствующий анализ справочника");
                        Require(LaboratoryTestDefinitionRules.IsWellFormedCode(payload.LoincCode) &&
                                payload.LoincCode.Trim() == payload.LoincCode &&
                                LaboratoryTestDefinitionRules.IsValidDisplayName(payload.DisplayName) &&
                                payload.DisplayName.Trim() == payload.DisplayName,
                            "некорректные исторические код или название анализа");
                        Require(LaboratoryAnalysisRules.IsValidComment(payload.Comment) &&
                                LaboratoryAnalysisRules.NormalizeComment(payload.Comment) == payload.Comment,
                            "некорректный комментарий анализа");
                        Require(payload.AuthoredAtUtc == item.OccurredAtUtc,
                            "время назначения анализа не совпадает со временем события");
                        Require(!laboratoryResultIds.Contains(payload.LaboratoryAnalysisRequestId) &&
                                laboratoryRequests.TryAdd(payload.LaboratoryAnalysisRequestId,
                                    new LaboratoryAnalysisRequestDto(payload.LaboratoryAnalysisRequestId,
                                        payload.MedicalRecordId, payload.PatientId, payload.RequestedByMedicalWorkerId,
                                        payload.LaboratoryTestDefinitionId, payload.LoincCode, payload.DisplayName,
                                        payload.Comment, payload.AuthoredAtUtc, ContractAnalysisStatus.Active, null,
                                        item.PerformedByNameSnapshot)),
                            "повторный идентификатор назначения анализа");
                        break;
                    }
                case MedicalRecordEventType.LaboratoryAnalysisResultRecorded:
                    {
                        var payload = Read<LaboratoryAnalysisResultRecordedPayload>(item);
                        RequireEntityId(item, payload.LaboratoryAnalysisResultId);
                        Require(laboratoryRequests.TryGetValue(payload.LaboratoryAnalysisRequestId, out var request),
                            "результат для отсутствующего назначения анализа");
                        Require(request!.PatientId == payload.PatientId && payload.PatientId == record.PatientId &&
                                request.LoincCode == payload.LoincCode,
                            "пациент или код результата не совпадают с назначением");
                        Require(request.Status == ContractAnalysisStatus.Active && request.Result is null,
                            "повторная регистрация результата анализа");
                        Require(payload.LaboratoryAnalysisResultId != payload.LaboratoryAnalysisRequestId &&
                                !laboratoryRequests.ContainsKey(payload.LaboratoryAnalysisResultId) &&
                                laboratoryResultIds.Add(payload.LaboratoryAnalysisResultId),
                            "повторный идентификатор результата анализа");
                        Require(payload.CreatedByServiceAccountId == serviceAuthor,
                            "сервисный автор результата не совпадает с автором события");
                        Require(LaboratoryAnalysisRules.IsValidValue(payload.Value) && payload.Value.Trim() == payload.Value &&
                                LaboratoryAnalysisRules.IsValidIssuedAt(payload.IssuedAtUtc),
                            "некорректное значение или время фиксации результата");
                        Require(payload.ReceivedAtUtc == item.OccurredAtUtc,
                            "время регистрации результата не совпадает со временем события");
                        laboratoryRequests[payload.LaboratoryAnalysisRequestId] = request with
                        {
                            Status = ContractAnalysisStatus.Completed,
                            Result = new LaboratoryAnalysisResultDto(payload.LaboratoryAnalysisResultId,
                                payload.LaboratoryAnalysisRequestId, payload.PatientId, payload.Value, payload.IssuedAtUtc,
                                payload.ReceivedAtUtc, payload.CreatedByServiceAccountId)
                        };
                        break;
                    }
                default:
                    throw Corrupted($"неизвестный тип события {item.EventType}");
            }

            version = item.Version;
            previousTime = item.OccurredAtUtc;
        }

        Require(version > 0, "отсутствует событие создания карты");
        Require(version == record.Version, "последняя версия истории не совпадает с версией карты");
        return snapshot ?? Capture();

        MedicalRecordSnapshotDto Capture() => new(record.Id, record.PatientId, at, version,
            diagnoses.Values.ToArray(), prescriptions.Values.ToArray())
        {
            LaboratoryAnalysisRequests = laboratoryRequests.Values.ToArray()
        };
    }

    private static T Read<T>(MedicalRecordHistoryEvent item) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(item.PayloadJson, PayloadOptions)
                   ?? throw Corrupted($"пустой payload события версии {item.Version}");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"История медицинской карты повреждена: неверный JSON версии {item.Version}.", exception);
        }
    }

    private static void RequireEntityId(MedicalRecordHistoryEvent item, Guid id) =>
        Require(id != Guid.Empty && item.EntityId == id, "идентификатор события не совпадает с payload");

    private static Guid RequireHumanAuthor(MedicalRecordHistoryEvent item)
    {
        Require(item.AuthorKind == DomainAuthorKind.MedicalWorker &&
                item.PerformedByMedicalWorkerId is { } id && id != Guid.Empty &&
                item.PerformedByServiceAccountId is null &&
                item.EventType != MedicalRecordEventType.LaboratoryAnalysisResultRecorded,
            "человеческое событие должно иметь одного автора-медработника");
        return item.PerformedByMedicalWorkerId.GetValueOrDefault();
    }

    private static Guid RequireServiceAuthor(MedicalRecordHistoryEvent item)
    {
        Require(item.AuthorKind == DomainAuthorKind.ServiceAccount &&
                item.PerformedByServiceAccountId is { } id && id != Guid.Empty &&
                item.PerformedByMedicalWorkerId is null &&
                item.EventType is MedicalRecordEventType.MedicalRecordCreated or MedicalRecordEventType.DiagnosisAdded or
                    MedicalRecordEventType.LaboratoryAnalysisResultRecorded,
            "событие должно иметь одного сервисного автора допустимого типа");
        return item.PerformedByServiceAccountId.GetValueOrDefault();
    }

    private static void RequireDiagnosisAuthor(MedicalRecordHistoryEvent item, DiagnosisAddedPayload payload)
    {
        if (item.AuthorKind == DomainAuthorKind.MedicalWorker && payload.AddedByMedicalWorkerId is null &&
            payload.AddedByServiceAccountId is null)
            return;
        Require(payload.AddedByMedicalWorkerId == item.PerformedByMedicalWorkerId &&
                payload.AddedByServiceAccountId == item.PerformedByServiceAccountId,
            "автор диагноза не совпадает с автором события");
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw Corrupted(detail);
    }

    private static InvalidOperationException Corrupted(string detail) =>
        new($"История медицинской карты повреждена: {detail}.");
}
