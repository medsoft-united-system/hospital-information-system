using HospitalInformationSystem.Application.Models.MedicalRecords;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Application.MedicalRecords.Mapping;

[Mapper]
public static partial class MedicalRecordMapping
{
    public static MedicalRecordDto ToDto(this MedicalRecord record) => MapRecord(record) with
    {
        LaboratoryAnalysisRequests = record.LaboratoryAnalysisRequests.ToDtos(record.HistoryEvents)
    };

    [MapperIgnoreSource(nameof(MedicalRecord.HistoryEvents))]
    [MapperIgnoreSource(nameof(MedicalRecord.LaboratoryAnalysisRequests))]
    [MapperIgnoreTarget(nameof(MedicalRecordDto.LaboratoryAnalysisRequests))]
    private static partial MedicalRecordDto MapRecord(MedicalRecord record);

    [MapProperty(nameof(MedicalRecordDiagnosis.DiagnosisCodeSnapshot), nameof(MedicalRecordDiagnosisDto.Code))]
    [MapProperty(nameof(MedicalRecordDiagnosis.DiagnosisNameSnapshot), nameof(MedicalRecordDiagnosisDto.Name))]
    [MapperIgnoreSource(nameof(MedicalRecordDiagnosis.MedicalRecordId))]
    [MapperIgnoreSource(nameof(MedicalRecordDiagnosis.DiagnosisDefinitionId))]
    [MapperIgnoreSource(nameof(MedicalRecordDiagnosis.RemovedAtUtc))]
    [MapperIgnoreSource(nameof(MedicalRecordDiagnosis.RemovedByMedicalWorkerId))]
    public static partial MedicalRecordDiagnosisDto ToDto(this MedicalRecordDiagnosis diagnosis);

    [MapperIgnoreSource(nameof(Prescription.MedicalRecordId))]
    [MapperIgnoreSource(nameof(Prescription.CancelledAtUtc))]
    [MapperIgnoreSource(nameof(Prescription.CancelledByMedicalWorkerId))]
    public static partial PrescriptionDto ToDto(this Prescription prescription);

    [MapProperty(nameof(LaboratoryAnalysisRequest.LoincCodeSnapshot), nameof(LaboratoryAnalysisRequestDto.LoincCode))]
    [MapProperty(nameof(LaboratoryAnalysisRequest.DisplayNameSnapshot), nameof(LaboratoryAnalysisRequestDto.DisplayName))]
    [MapperIgnoreTarget(nameof(LaboratoryAnalysisRequestDto.RequestedByNameSnapshot))]
    public static partial LaboratoryAnalysisRequestDto ToDto(this LaboratoryAnalysisRequest request);

    public static IReadOnlyList<LaboratoryAnalysisRequestDto> ToDtos(
        this IReadOnlyCollection<LaboratoryAnalysisRequest> requests,
        IReadOnlyCollection<MedicalRecordHistoryEvent> historyEvents)
    {
        var authors = historyEvents.Where(x => x.EventType == MedicalRecordEventType.LaboratoryAnalysisRequested &&
                                              x.AuthorKind == Models.MedicalRecords.MedicalRecordAuthorKind.MedicalWorker &&
                                              x.EntityId.HasValue)
            .GroupBy(x => x.EntityId!.Value).ToDictionary(x => x.Key, x => x.OrderBy(item => item.Version).First());
        return requests.Select(request => request.ToDto() with
        {
            RequestedByNameSnapshot = authors.TryGetValue(request.Id, out var author) &&
                                      author.PerformedByMedicalWorkerId == request.RequestedByMedicalWorkerId
                ? author.PerformedByNameSnapshot : null
        }).ToArray();
    }

    public static partial LaboratoryAnalysisResultDto ToDto(this LaboratoryAnalysisResult result);



    [MapProperty(nameof(MedicalRecordHistoryEvent.PerformedByNameSnapshot), nameof(MedicalRecordHistoryEventDto.PerformedByName))]
    [MapProperty(nameof(MedicalRecordHistoryEvent.PerformedByPositionSnapshot), nameof(MedicalRecordHistoryEventDto.PerformedByPosition))]
    [MapperIgnoreSource(nameof(MedicalRecordHistoryEvent.Id))]
    [MapperIgnoreSource(nameof(MedicalRecordHistoryEvent.MedicalRecordId))]
    public static partial MedicalRecordHistoryEventDto ToDto(this MedicalRecordHistoryEvent historyEvent);

    private static IReadOnlyCollection<MedicalRecordDiagnosisDto> MapCurrentDiagnoses(
        IReadOnlyCollection<MedicalRecordDiagnosis> diagnoses) =>
        diagnoses.Where(x => x.RemovedAtUtc is null).Select(x => x.ToDto()).ToArray();

    private static IReadOnlyCollection<PrescriptionDto> MapCurrentPrescriptions(
        IReadOnlyCollection<Prescription> prescriptions) =>
        prescriptions.Where(x => x.CancelledAtUtc is null).Select(x => x.ToDto()).ToArray();
}
