using System.Globalization;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.Patients;
using static HospitalInformationSystem.Api.Hl7.Hl7Profile;

namespace HospitalInformationSystem.Api.Hl7;

internal static class Hl7PatientMapper
{
    public static ImportedPatientInput Map(string[] pid, string[][] dg1)
    {
        OnlyFields(pid, 3, 5, 7, 8);
        var id = Hl7Text.Components(Field(pid, 3), 5);
        Require(id[1] == "" && id[2] == "" && id[3] == Hl7ImportProtocol.SourceSystem && id[4] == "PI");
        var externalId = RequiredClinical(id[0], 128);
        if (externalId != externalId.Trim())
            throw new Hl7DecodeException(Hl7FailureKind.Validation);
        try
        {
            return MapFields(pid, dg1, id[3], externalId);
        }
        catch (Hl7DecodeException exception)
        {
            throw new Hl7DecodeException(exception.Kind, exception.Header, externalId);
        }
    }

    private static ImportedPatientInput MapFields(string[] pid, string[][] dg1, string sourceSystem, string externalId)
    {
        var name = Hl7Text.Components(Field(pid, 5), 3);
        var lastName = RequiredClinical(name[0], PatientFieldLimits.MaxNameLength);
        var firstName = RequiredClinical(name[1], PatientFieldLimits.MaxNameLength);
        if (name[2].Length > PatientFieldLimits.MaxNameLength) throw new Hl7DecodeException(Hl7FailureKind.Validation);
        if (!DateOnly.TryParseExact(Field(pid, 7), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birthDate))
            throw new Hl7DecodeException(Hl7FailureKind.Validation);
        var sex = Hl7Text.Atom(Field(pid, 8)) switch
        {
            "M" => AdministrativeSex.Male,
            "F" => AdministrativeSex.Female,
            "U" => AdministrativeSex.Unknown,
            _ => throw new Hl7DecodeException(Hl7FailureKind.Validation)
        };
        var diagnoses = new List<ImportedDiagnosisInput>(dg1.Length);
        for (var index = 0; index < dg1.Length; index++)
        {
            var segment = dg1[index];
            OnlyFields(segment, 1, 3, 5, 6);
            Require(Field(segment, 1) == (index + 1).ToString(CultureInfo.InvariantCulture));
            var diagnosis = Hl7Text.Components(Field(segment, 3), 3);
            var type = Hl7Text.Components(Field(segment, 6), 3);
            Require(diagnosis[2] == Hl7ImportProtocol.DiagnosisCodingSystem &&
                string.Join('^', type) == "U^Unspecified^99MEDSOFT");
            diagnoses.Add(new ImportedDiagnosisInput
            {
                Code = RequiredClinical(diagnosis[0], 32, 3),
                Title = RequiredClinical(diagnosis[1], 256, 3),
                CodingSystem = diagnosis[2],
                DiagnosedAt = ReadTime(Field(segment, 5), Hl7FailureKind.Validation),
                DiagnosisType = type[0]
            });
        }
        return new ImportedPatientInput
        {
            SourceSystem = sourceSystem,
            ExternalPatientId = externalId,
            LastName = lastName,
            FirstName = firstName,
            MiddleName = name[2].Length == 0 ? null : name[2],
            BirthDate = birthDate,
            Sex = sex,
            Diagnoses = diagnoses
        };
    }

    private static string RequiredClinical(string value, int max, int min = 1)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "\"\"" || value.Length > max || value.Trim().Length < min)
            throw new Hl7DecodeException(Hl7FailureKind.Validation);
        return value;
    }
}
