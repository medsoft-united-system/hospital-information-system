using HospitalInformationSystem.Application.Patients.Services;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Patients;

namespace HospitalInformationSystem.Application.Patients.Factories;

public static class PatientFactory
{
    public static Patient Create(string firstName, string lastName, string? middleName,
        DateOnly birthDate, AdministrativeSex sex, string? comment, DateTimeOffset now)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid(), CreatedAtUtc = now.ToUniversalTime(),
            FirstName = string.Empty, LastName = string.Empty
        };
        PatientService.Update(patient, firstName, lastName, middleName, birthDate, sex, comment, now);
        return patient;
    }
}
