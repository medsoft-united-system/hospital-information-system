using System.Globalization;
using HospitalInformationSystem.Contracts.MedicalRecords;

namespace HospitalInformationSystem.Web.Components.MedicalRecords;

public static class LaboratoryAnalysisPresentation
{
    public static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static IReadOnlyDictionary<Guid, string> RequesterNames(IEnumerable<MedicalRecordHistoryEventDto> history) =>
        history.Where(x => x.EventType == "LaboratoryAnalysisRequested" &&
                           x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
                           x.EntityId.HasValue && !string.IsNullOrWhiteSpace(x.PerformedByName))
            .GroupBy(x => x.EntityId!.Value).ToDictionary(x => x.Key,
                x => x.OrderBy(y => y.Version).First().PerformedByName);
}
