using System.Globalization;

namespace HospitalInformationSystem.Web.Components.MedicalRecords;

public static class LaboratoryAnalysisPresentation
{
    public static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
}
