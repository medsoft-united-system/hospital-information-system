namespace HospitalInformationSystem.Application.Models.Security;

public sealed record ConfirmSetupRequest(Guid ChallengeId, string Code);
