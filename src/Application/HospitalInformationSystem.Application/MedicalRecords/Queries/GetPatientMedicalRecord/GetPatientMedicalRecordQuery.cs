using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Queries.GetPatientMedicalRecord;

public sealed record GetPatientMedicalRecordQuery(Guid PatientId) : IRequest<MedicalRecordDto>;
