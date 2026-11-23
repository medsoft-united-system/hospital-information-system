namespace HospitalInformationSystem.Application.Models.Common;

public sealed class DomainValidationException(string message) : Exception(message);
