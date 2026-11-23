namespace HospitalInformationSystem.Models.Common;

public sealed class DomainEntityNotFoundException(string message) : Exception(message);
