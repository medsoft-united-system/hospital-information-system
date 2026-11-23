using HospitalInformationSystem.Application.Models.Common;

namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed class LaboratoryAnalysisResultAlreadyExistsException()
    : BusinessRuleConflictException("Результат этого анализа уже зарегистрирован.");
