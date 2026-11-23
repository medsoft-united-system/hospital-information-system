using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Contracts.Abstractions;

public interface ICurrentServiceAccount
{
    ServiceAccountIdentity RequireIdentity();
}
