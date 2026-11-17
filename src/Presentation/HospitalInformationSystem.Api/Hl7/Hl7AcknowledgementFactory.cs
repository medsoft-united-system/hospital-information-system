using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Contracts.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7;

public static class Hl7AcknowledgementFactory
{
    internal const string DuplicateText = "DUPLICATE: пациент уже импортирован";
    internal const string ValidationText = "VALIDATION: проверьте данные пациента и диагнозов";
    internal const string AuthenticationText = "AUTH: доступ запрещён";
    internal const string ProtocolText = "PROTOCOL: сообщение не соответствует профилю C4-v1";
    internal const string InternalText = "INTERNAL: импорт не выполнен";
    internal const string AuthenticationErrorCode = "207";

    public static Hl7Acknowledgement Imported(PatientImported result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.PatientId == Guid.Empty || result.DiagnosisCount < 0)
            throw new ArgumentException("Некорректный результат импорта.", nameof(result));
        return new Hl7Acknowledgement(Hl7AcknowledgementCode.AA, result.PatientId.ToString("D"));
    }

    public static Hl7Acknowledgement Rejected(PatientImportRejected result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Reason switch
        {
            PatientImportRejection.Duplicate => new Hl7Acknowledgement(Hl7AcknowledgementCode.AE, DuplicateText),
            PatientImportRejection.Validation => ValidationFailure(),
            _ => throw new ArgumentException("Некорректная причина отказа импорта.", nameof(result))
        };
    }

    public static Hl7Acknowledgement AuthenticationFailure() =>
        new(Hl7AcknowledgementCode.AR, AuthenticationText, AuthenticationErrorCode);

    public static Hl7Acknowledgement ProtocolFailure() => new(Hl7AcknowledgementCode.AR, ProtocolText);

    public static Hl7Acknowledgement ValidationFailure() => new(Hl7AcknowledgementCode.AE, ValidationText);

    public static Hl7Acknowledgement InternalFailure() => new(Hl7AcknowledgementCode.AE, InternalText);

    public static Hl7Acknowledgement ProbeAccepted() => new(Hl7AcknowledgementCode.AA, Hl7ImportProtocol.ProbeSuccess);
}
