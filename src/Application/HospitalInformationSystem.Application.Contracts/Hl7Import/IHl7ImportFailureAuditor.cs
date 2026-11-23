using HospitalInformationSystem.Application.Models.Hl7Import;
namespace HospitalInformationSystem.Application.Contracts.Hl7Import;

public interface IHl7ImportFailureAuditor
{
    Task<bool> RecordAsync(Hl7ImportFailure failure, CancellationToken cancellationToken);
}
