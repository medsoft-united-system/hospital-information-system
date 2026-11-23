namespace HospitalInformationSystem.Application.Models.Common;

public sealed class EntityNotFoundException(string message) : Exception(message);
