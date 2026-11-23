namespace HospitalInformationSystem.Models.Auth;

public sealed record CreateAccountRequest(string UserName, string Password, string Role, Guid? MedicalWorkerId);
