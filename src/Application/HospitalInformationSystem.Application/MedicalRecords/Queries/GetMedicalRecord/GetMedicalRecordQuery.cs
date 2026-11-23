using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.MedicalRecords.Queries.GetMedicalRecord;

public sealed record GetMedicalRecordQuery(Guid Id) : IRequest<MedicalRecordDto>;
