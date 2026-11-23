namespace HospitalInformationSystem.Application.Models.Security;

public sealed record BeginSetupRequest(string Password, string ConfirmPassword);
