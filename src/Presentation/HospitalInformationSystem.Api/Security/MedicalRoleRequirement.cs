using HospitalInformationSystem.Contracts.Security;
using Microsoft.AspNetCore.Authorization;

namespace HospitalInformationSystem.Api.Security;

public sealed record MedicalRoleRequirement(params MedicalWorkerRole[] AllowedRoles) : IAuthorizationRequirement;
