using System.Text;
using HospitalInformationSystem.Models.Hl7Import;
using static HospitalInformationSystem.Api.Hl7.Hl7Profile;

namespace HospitalInformationSystem.Api.Hl7;

public sealed class Hl7MessageCodec(TimeProvider timeProvider) : IHl7MessageCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Hl7AcknowledgementEncoder _acknowledgements = new(timeProvider);

    public Hl7MessageEnvelope DecodeEnvelope(ReadOnlyMemory<byte> payload)
    {
        var wire = Hl7WireReader.Read(payload);
        return Envelope(wire.Header, wire.Segments);
    }

    public Hl7IncomingMessage Decode(ReadOnlyMemory<byte> payload)
    {
        var wire = Hl7WireReader.Read(payload);
        try
        {
            var envelope = Envelope(wire.Header, wire.Segments);
            var segments = wire.Segments;
            ImportedPatientInput? patient = null;
            if (wire.Header.MessageType == "NMD")
            {
                Require(segments.Length == 3 && segments[2][0] == "NCK");
                OnlyFields(segments[2], 1);
                ReadTime(Field(segments[2], 1), Hl7FailureKind.Protocol);
            }
            else
            {
                Require(segments.Length >= 5 && segments.Length <= 5 + Hl7ImportProtocol.MaxDiagnoses &&
                    segments[2][0] == "EVN" && segments[3][0] == "PID" && segments[4][0] == "PV1" &&
                    segments.Skip(5).All(segment => segment[0] == "DG1"));
                OnlyFields(segments[2], 2);
                Require(ReadTime(Field(segments[2], 2), Hl7FailureKind.Protocol).EqualsExact(wire.Header.SentAt));
                OnlyFields(segments[4], 2);
                Require(Hl7Text.Atom(Field(segments[4], 2)) == "N");
                patient = Hl7PatientMapper.Map(segments[3], segments.Skip(5).ToArray());
            }
            return new Hl7IncomingMessage(envelope.AccessToken) { Header = wire.Header, Patient = patient };
        }
        catch (Hl7DecodeException exception) when (exception.Header is null)
        {
            throw new Hl7DecodeException(exception.Kind, wire.Header, exception.ExternalPatientId);
        }
    }

    public byte[] EncodeAcknowledgement(Hl7MessageHeader request, Hl7Acknowledgement acknowledgement) =>
        _acknowledgements.Encode(request, acknowledgement);

    private static Hl7MessageEnvelope Envelope(Hl7MessageHeader header, string[][] segments)
    {
        try
        {
            Require((header.MessageType == "ADT" && header.TriggerEvent == "A28" && header.MessageStructure == "ADT_A05") ||
                (header.MessageType == "NMD" && header.TriggerEvent == "N02" && header.MessageStructure == "NMD_N02"));
            if (segments.Length < 2 || segments[1][0] != "UAC" ||
                segments.Count(segment => segment[0] == "UAC") != 1)
                throw new Hl7DecodeException(Hl7FailureKind.Authentication);
            return new Hl7MessageEnvelope(Token(segments[1])) { Header = header };
        }
        catch (Hl7DecodeException exception) when (exception.Header is null)
        {
            throw new Hl7DecodeException(exception.Kind, header, exception.ExternalPatientId);
        }
    }

    private static string Token(string[] uac)
    {
        try
        {
            OnlyFields(uac, 1, 2);
            var kind = Hl7Text.Components(Field(uac, 1), 3);
            var credential = Hl7Text.Components(Field(uac, 2), 5);
            if (string.Join('^', kind) != Hl7ImportProtocol.CredentialType || credential[0] != "MedSoftDesktop" ||
                credential[1] != Hl7ImportProtocol.CredentialMediaType ||
                credential[2] != Hl7ImportProtocol.CredentialMediaSubtype ||
                credential[3] != Hl7ImportProtocol.CredentialEncoding ||
                credential[4].Length is 0 or > 32768 || credential[4].Any(char.IsWhiteSpace))
                throw new Hl7DecodeException(Hl7FailureKind.Authentication);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(credential[4]); }
            catch (FormatException) { throw new Hl7DecodeException(Hl7FailureKind.Authentication); }
            if (Convert.ToBase64String(bytes) != credential[4])
                throw new Hl7DecodeException(Hl7FailureKind.Authentication);
            string token;
            try { token = Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new Hl7DecodeException(Hl7FailureKind.Authentication); }
            if (token.Length is 0 or > 16384 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                token[0] == '\uFEFF')
                throw new Hl7DecodeException(Hl7FailureKind.Authentication);
            return token;
        }
        catch (Hl7DecodeException)
        {
            throw new Hl7DecodeException(Hl7FailureKind.Authentication);
        }
    }
}
