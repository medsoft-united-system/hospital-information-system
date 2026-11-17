using System.Globalization;
using System.Text;
using HospitalInformationSystem.Contracts.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7AcknowledgementEncoder(TimeProvider timeProvider)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public byte[] Encode(Hl7MessageHeader request, Hl7Acknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ValidateHeader(request);
        ValidateAcknowledgement(request, acknowledgement);
        var timestamp = timeProvider.GetUtcNow().ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "+0000";
        var controlId = Guid.NewGuid().ToString("N");
        var payload = new StringBuilder();
        payload.Append("MSH|^~\\&|").Append(Hl7Text.Escape(request.ReceivingApplication)).Append('|')
            .Append(Hl7Text.Escape(request.ReceivingFacility)).Append('|')
            .Append(Hl7Text.Escape(request.SendingApplication)).Append('|')
            .Append(Hl7Text.Escape(request.SendingFacility)).Append('|').Append(timestamp)
            .Append("||ACK^").Append(Hl7Text.Escape(request.TriggerEvent)).Append("^ACK|")
            .Append(controlId).Append("|P|").Append(Hl7ImportProtocol.Version)
            .Append("||||||").Append(Hl7ImportProtocol.CharacterSet).Append('\r');
        payload.Append("MSA|").Append(acknowledgement.Code).Append('|')
            .Append(Hl7Text.Escape(request.ControlId)).Append('|')
            .Append(Hl7Text.Escape(acknowledgement.Text)).Append('\r');
        if (acknowledgement.ErrorCode == Hl7AcknowledgementFactory.AuthenticationErrorCode)
            payload.Append("ERR|||207^Application internal error^HL70357|E|||Доступ запрещён\r");
        return Utf8.GetBytes(payload.ToString());
    }

    private static void ValidateHeader(Hl7MessageHeader request)
    {
        var atoms = new[]
        {
            request.MessageType, request.TriggerEvent, request.ControlId,
            request.SendingApplication, request.SendingFacility, request.ReceivingApplication, request.ReceivingFacility
        };
        if (atoms.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) ||
            request.MessageStructure is null || request.MessageStructure.Length > 128 ||
            request.MessageStructure.Any(char.IsControl))
            throw new ArgumentException("Некорректный заголовок сообщения HL7.", nameof(request));
    }

    private static void ValidateAcknowledgement(Hl7MessageHeader request, Hl7Acknowledgement acknowledgement)
    {
        var valid = acknowledgement.Code switch
        {
            Hl7AcknowledgementCode.AA => acknowledgement.ErrorCode is null && ValidSuccess(request, acknowledgement.Text),
            Hl7AcknowledgementCode.AE => acknowledgement.ErrorCode is null && acknowledgement.Text is
                Hl7AcknowledgementFactory.DuplicateText or Hl7AcknowledgementFactory.ValidationText or
                Hl7AcknowledgementFactory.InternalText,
            Hl7AcknowledgementCode.AR =>
                (acknowledgement.Text == Hl7AcknowledgementFactory.AuthenticationText &&
                 acknowledgement.ErrorCode == Hl7AcknowledgementFactory.AuthenticationErrorCode) ||
                (acknowledgement.Text == Hl7AcknowledgementFactory.ProtocolText && acknowledgement.ErrorCode is null),
            _ => false
        };
        if (!valid)
            throw new ArgumentException("Некорректное подтверждение HL7.", nameof(acknowledgement));
    }

    private static bool ValidSuccess(Hl7MessageHeader request, string text)
    {
        if (request.MessageType == "NMD" && request.TriggerEvent == "N02" && request.MessageStructure == "NMD_N02")
            return text == Hl7ImportProtocol.ProbeSuccess;
        return request.MessageType == "ADT" && request.TriggerEvent == "A28" && request.MessageStructure == "ADT_A05" &&
               Guid.TryParseExact(text, "D", out var patientId) && patientId != Guid.Empty;
    }
}
