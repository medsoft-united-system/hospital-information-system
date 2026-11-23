namespace HospitalInformationSystem.Application.Models.Common;

public sealed class BusinessConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);
