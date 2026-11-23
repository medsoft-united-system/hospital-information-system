using HospitalInformationSystem.Models.Common;

namespace HospitalInformationSystem.Models.MedicalRecords;

public sealed class LaboratoryAnalysisResultAlreadyExistsException()
    : BusinessRuleConflictException("Результат этого анализа уже зарегистрирован.");
