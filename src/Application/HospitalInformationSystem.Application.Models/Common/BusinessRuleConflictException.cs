namespace HospitalInformationSystem.Application.Models.Common;

public class BusinessRuleConflictException(string message) : Exception(message);
