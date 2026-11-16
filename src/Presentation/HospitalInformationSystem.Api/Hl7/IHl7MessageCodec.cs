namespace HospitalInformationSystem.Api.Hl7;

public interface IHl7MessageCodec
{
    Hl7IncomingMessage Decode(ReadOnlyMemory<byte> payload);

    // Payload includes segment CR terminators. MLLP framing belongs to the TCP adapter.
    byte[] EncodeAcknowledgement(Hl7MessageHeader request, Hl7Acknowledgement acknowledgement);
}
