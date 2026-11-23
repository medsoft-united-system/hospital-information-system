namespace HospitalInformationSystem.Models.Common;

public sealed class DomainValidationException(string message) : Exception(message);
