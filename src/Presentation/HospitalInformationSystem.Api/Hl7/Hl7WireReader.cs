using System.Text;
using HospitalInformationSystem.Contracts.Hl7Import;
using static HospitalInformationSystem.Api.Hl7.Hl7Profile;

namespace HospitalInformationSystem.Api.Hl7;

internal sealed record Hl7WireMessage(Hl7MessageHeader Header, string[][] Segments);

internal static class Hl7WireReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Hl7WireMessage Read(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > Hl7ImportProtocol.MaxPayloadBytes)
            throw new Hl7DecodeException(Hl7FailureKind.Protocol);
        string text;
        try { text = Utf8.GetString(payload.Span); }
        catch (DecoderFallbackException) { throw new Hl7DecodeException(Hl7FailureKind.Protocol); }
        if (text[0] == '\uFEFF' || text[^1] != '\r' ||
            text.Any(c => char.IsControl(c) && c != '\r'))
            throw new Hl7DecodeException(Hl7FailureKind.Protocol);
        var lines = text[..^1].Split('\r');
        if (lines.Any(line => line.Length == 0) || lines.Length > 5 + Hl7ImportProtocol.MaxDiagnoses ||
            !lines[0].StartsWith("MSH|^~\\&|", StringComparison.Ordinal))
            throw new Hl7DecodeException(Hl7FailureKind.Protocol);
        var msh = new[] { "MSH", "^~\\&" }.Concat(Hl7Text.Split(lines[0][9..], '|')).ToArray();
        var header = Header(msh);
        try
        {
            Require(Field(msh, 10) == "P" && Field(msh, 11) == Hl7ImportProtocol.Version &&
                Field(msh, 17) == Hl7ImportProtocol.CharacterSet);
            OnlyFields(msh, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 17);
            Require(header.SendingApplication == Hl7ImportProtocol.SendingApplication &&
                header.SendingFacility == Hl7ImportProtocol.SourceSystem &&
                header.ReceivingApplication == Hl7ImportProtocol.ReceivingApplication &&
                header.ReceivingFacility == Hl7ImportProtocol.ReceivingFacility);
            var segments = new string[lines.Length][];
            segments[0] = msh;
            for (var index = 1; index < lines.Length; index++)
            {
                try { segments[index] = Hl7Text.Split(lines[index], '|'); }
                catch (Hl7DecodeException) when (lines[index].StartsWith("UAC|", StringComparison.Ordinal))
                {
                    throw new Hl7DecodeException(Hl7FailureKind.Authentication, header);
                }
            }
            return new Hl7WireMessage(header, segments);
        }
        catch (Hl7DecodeException exception) when (exception.Header is null)
        {
            throw new Hl7DecodeException(exception.Kind, header);
        }
    }

    private static Hl7MessageHeader Header(string[] msh)
    {
        var type = Hl7Text.Components(Field(msh, 8), 3);
        var sentAtValid = Hl7Timestamp.TryRead(Field(msh, 6), out var sentAt);
        var header = new Hl7MessageHeader
        {
            MessageType = Required(type[0], 128), TriggerEvent = Required(type[1], 128),
            MessageStructure = type[2].Length <= 128 ? type[2] :
                throw new Hl7DecodeException(Hl7FailureKind.Protocol),
            ControlId = Required(Hl7Text.Atom(Field(msh, 9)), 128),
            SendingApplication = Required(Hl7Text.Atom(Field(msh, 2)), 128),
            SendingFacility = Required(Hl7Text.Atom(Field(msh, 3)), 128),
            ReceivingApplication = Required(Hl7Text.Atom(Field(msh, 4)), 128),
            ReceivingFacility = Required(Hl7Text.Atom(Field(msh, 5)), 128),
            SentAt = sentAtValid ? sentAt : DateTimeOffset.UnixEpoch
        };
        if (!sentAtValid) throw new Hl7DecodeException(Hl7FailureKind.Protocol, header);
        return header;
    }
}
