using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Contracts.Abstractions;

public interface ICurrentServiceAccount
{
    ServiceAccountIdentity RequireIdentity();
}
