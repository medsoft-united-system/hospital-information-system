using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Persistence;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Persistence.Mappings;

[Mapper]
internal static partial class MedicalRecordDboMapper
{
    public static partial MedicalRecord ToDomain(this MedicalRecordDbo source);
    public static partial MedicalRecordDbo ToDbo(this MedicalRecord source);
    public static partial MedicalRecordDiagnosis ToDomain(this MedicalRecordDiagnosisDbo source);
    public static partial MedicalRecordDiagnosisDbo ToDbo(this MedicalRecordDiagnosis source);
    public static partial Prescription ToDomain(this PrescriptionDbo source);
    public static partial PrescriptionDbo ToDbo(this Prescription source);
    public static partial MedicalRecordHistoryEvent ToDomain(this MedicalRecordHistoryEventDbo source);
    public static partial MedicalRecordHistoryEventDbo ToDbo(this MedicalRecordHistoryEvent source);
    public static partial LaboratoryAnalysisRequest ToDomain(this LaboratoryAnalysisRequestDbo source);
    public static partial LaboratoryAnalysisRequestDbo ToDbo(this LaboratoryAnalysisRequest source);
    public static partial LaboratoryAnalysisResult ToDomain(this LaboratoryAnalysisResultDbo source);
    public static partial LaboratoryAnalysisResultDbo ToDbo(this LaboratoryAnalysisResult source);

    public static partial void UpdateDbo(this MedicalRecordDiagnosis source, MedicalRecordDiagnosisDbo target);
    public static partial void UpdateDbo(this Prescription source, PrescriptionDbo target);
}
