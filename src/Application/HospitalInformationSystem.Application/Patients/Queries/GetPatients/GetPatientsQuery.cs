using HospitalInformationSystem.Models.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Patients.Queries.GetPatients;

public sealed record GetPatientsQuery : IRequest<IReadOnlyList<PatientDto>>;
