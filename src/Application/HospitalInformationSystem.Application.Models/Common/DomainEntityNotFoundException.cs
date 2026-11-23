namespace HospitalInformationSystem.Application.Models.Common;

public sealed class DomainEntityNotFoundException(string message) : Exception(message);
