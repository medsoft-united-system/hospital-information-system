using System.Buffers;
using HospitalInformationSystem.Models.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public static class MllpFraming
{
    private const int BufferSize = 8192;

    public static async Task<byte[]> ReadAsync(Stream stream, int maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxPayloadBytes is < 1 or > Hl7ImportProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "Invalid MLLP payload limit.");
        cancellationToken.ThrowIfCancellationRequested();

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var payload = new MemoryStream(Math.Min(maxPayloadBytes, BufferSize));
        var started = false;
        var requiresFinalCr = false;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (count == 0) throw new MllpFrameException();
                var start = 0;
                if (!started)
                {
                    if (buffer[0] != Hl7ImportProtocol.FrameStart) throw new MllpFrameException();
                    started = true;
                    start = 1;
                }

                for (var index = start; index < count; index++)
                {
                    var current = buffer[index];
                    if (requiresFinalCr)
                    {
                        if (current != Hl7ImportProtocol.SegmentTerminator ||
                            index != count - 1 || payload.Length == 0)
                            throw new MllpFrameException();
                        cancellationToken.ThrowIfCancellationRequested();
                        return payload.ToArray();
                    }
                    if (current is 0 or Hl7ImportProtocol.FrameStart)
                        throw new MllpFrameException();
                    if (current == Hl7ImportProtocol.FrameEnd)
                    {
                        Append(payload, buffer, start, index - start, maxPayloadBytes);
                        requiresFinalCr = true;
                        start = index + 1;
                    }
                }
                if (!requiresFinalCr)
                    Append(payload, buffer, start, count - start, maxPayloadBytes);
            }
        }
        finally
        {
            if (payload.TryGetBuffer(out var contents))
                contents.AsSpan().Clear();
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.IsEmpty || payload.Length > Hl7ImportProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "Invalid MLLP payload size.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePayload(payload.Span);

        await stream.WriteAsync(new byte[] { Hl7ImportProtocol.FrameStart }, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.WriteAsync(
            new byte[] { Hl7ImportProtocol.FrameEnd, Hl7ImportProtocol.SegmentTerminator }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void Append(MemoryStream payload, byte[] buffer, int offset, int count, int maxPayloadBytes)
    {
        if (count > maxPayloadBytes - payload.Length)
            throw new MllpFrameException();
        payload.Write(buffer, offset, count);
    }

    private static void ValidatePayload(ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload)
            if (value is 0 or Hl7ImportProtocol.FrameStart or Hl7ImportProtocol.FrameEnd)
                throw new MllpFrameException();
    }
}
