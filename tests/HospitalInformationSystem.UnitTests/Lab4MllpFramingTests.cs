using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7.Transport;
using HospitalInformationSystem.Contracts.Hl7Import;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4MllpFramingTests
{
    private const string SensitivePayload = "secret-access-token";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Read_FragmentedFrame_ShouldPreserveEveryPayloadByte(int chunkSize)
    {
        var frame = Fixture("adt-escaped-synthetic.mllp");
        await using var stream = new FragmentedReadStream(frame, chunkSize);

        var payload = await MllpFraming.ReadAsync(stream, Hl7ImportProtocol.MaxPayloadBytes,
            CancellationToken.None);

        payload.Should().Equal(Fixture("adt-escaped-synthetic.hl7"));
    }

    [Fact]
    public async Task Read_AllCompleteFixtureFrames_ShouldMatchTheirExactPayloads()
    {
        foreach (var path in Directory.GetFiles(FixtureDirectory, "*.hl7"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == "malformed-frame-payload") continue;
            var framePath = Path.Combine(FixtureDirectory, name + ".mllp");
            if (!File.Exists(framePath)) continue;
            await using var stream = new MemoryStream(File.ReadAllBytes(framePath));

            var actual = await MllpFraming.ReadAsync(stream, Hl7ImportProtocol.MaxPayloadBytes,
                CancellationToken.None);

            actual.Should().Equal(File.ReadAllBytes(path), "the wire bytes of {0} must round-trip", name);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(8192)]
    [InlineData(1048576)]
    public async Task Read_ExactPayloadLimit_ShouldExcludeFramingBytes(int size)
    {
        var payload = Enumerable.Repeat((byte)'A', size).ToArray();
        await using var stream = new MemoryStream(Frame(payload));

        var actual = await MllpFraming.ReadAsync(stream, size, CancellationToken.None);

        actual.Should().Equal(payload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(8192)]
    public async Task Read_OverLimit_ShouldRejectWithoutReturningPartialPayload(int limit)
    {
        var frame = Frame(Enumerable.Repeat((byte)'A', limit + 1).ToArray());
        await using var stream = new FragmentedReadStream(frame, 7);
        var read = () => MllpFraming.ReadAsync(stream, limit, CancellationToken.None);

        await read.Should().ThrowAsync<MllpFrameException>();
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public async Task Read_MalformedFrame_ShouldRejectWithSafeDiagnostics(string caseName, byte[] frame)
    {
        await using var stream = new MemoryStream(frame);
        var read = () => MllpFraming.ReadAsync(stream, Hl7ImportProtocol.MaxPayloadBytes,
            CancellationToken.None);

        var exception = (await read.Should().ThrowAsync<MllpFrameException>(caseName)).Which;
        exception.ToString().Should().NotContain(SensitivePayload);
        exception.InnerException.Should().BeNull();
    }

    public static IEnumerable<object[]> MalformedFrames()
    {
        var payload = Encoding.UTF8.GetBytes(SensitivePayload);
        yield return ["empty-stream", Array.Empty<byte>()];
        yield return ["start-only", new byte[] { 0x0B }];
        yield return ["empty-payload", new byte[] { 0x0B, 0x1C, 0x0D }];
        yield return ["missing-start", payload.Concat(new byte[] { 0x1C, 0x0D }).ToArray()];
        yield return ["missing-end", new byte[] { 0x0B }.Concat(payload).ToArray()];
        yield return ["incomplete-end", new byte[] { 0x0B }.Concat(payload).Concat(new byte[] { 0x1C }).ToArray()];
        yield return ["wrong-end-cr", new byte[] { 0x0B }.Concat(payload).Concat(new byte[] { 0x1C, 0x0A }).ToArray()];
        yield return ["embedded-nul", Frame(payload.Concat(new byte[] { 0 }).ToArray())];
        yield return ["nested-start", Frame(payload.Concat(new byte[] { 0x0B }).ToArray())];
        yield return ["trailing-byte", Frame(payload).Concat(new byte[] { (byte)'X' }).ToArray()];
        yield return ["coalesced-two-frames", Frame(payload).Concat(Frame(payload)).ToArray()];
    }

    [Fact]
    public async Task Read_IncompleteFixture_ShouldRejectAfterEof()
    {
        await using var stream = new FragmentedReadStream(Fixture("malformed-frame-payload.mllp"), 1);
        var read = () => MllpFraming.ReadAsync(stream, Hl7ImportProtocol.MaxPayloadBytes,
            CancellationToken.None);

        await read.Should().ThrowAsync<MllpFrameException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task Read_InvalidLimit_ShouldFailBeforeReading(int limit)
    {
        await using var stream = new MemoryStream(Frame(Encoding.UTF8.GetBytes("MSH")));
        var read = () => MllpFraming.ReadAsync(stream, limit, CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public async Task Read_CanceledToken_ShouldPropagateBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Frame(Encoding.UTF8.GetBytes("MSH")));
        var read = () => MllpFraming.ReadAsync(stream, 32, cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        stream.Position.Should().Be(0);
    }

    [Fact]
    public async Task Read_CancellationDuringPendingRead_ShouldEndTheRead()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new PendingReadStream();
        var read = MllpFraming.ReadAsync(stream, 32, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        var awaitRead = () => read;
        await awaitRead.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Write_AllFixturePayloads_ShouldMatchTheirExactFrames()
    {
        foreach (var path in Directory.GetFiles(FixtureDirectory, "*.hl7"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == "malformed-frame-payload") continue;
            var framePath = Path.Combine(FixtureDirectory, name + ".mllp");
            if (!File.Exists(framePath)) continue;
            await using var stream = new MemoryStream();

            await MllpFraming.WriteAsync(stream, File.ReadAllBytes(path), CancellationToken.None);

            stream.ToArray().Should().Equal(File.ReadAllBytes(framePath),
                "the frame of {0} must contain only the required markers", name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_InvalidSize_ShouldFailBeforeWriting(bool oversized)
    {
        var payload = oversized ? new byte[Hl7ImportProtocol.MaxPayloadBytes + 1] : Array.Empty<byte>();
        await using var stream = new MemoryStream();
        var write = () => MllpFraming.WriteAsync(stream, payload, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentOutOfRangeException>();
        stream.Length.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x0B)]
    [InlineData(0x1C)]
    public async Task Write_EmbeddedFramingControl_ShouldFailBeforeWriting(int control)
    {
        var payload = new byte[] { (byte)'A', (byte)control, (byte)'B' };
        await using var stream = new MemoryStream();
        var write = () => MllpFraming.WriteAsync(stream, payload, CancellationToken.None);

        await write.Should().ThrowAsync<MllpFrameException>();
        stream.Length.Should().Be(0);
    }

    [Fact]
    public async Task Write_CanceledToken_ShouldPropagateBeforeWriting()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream();
        var write = () => MllpFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("MSH"), cancellation.Token);

        await write.Should().ThrowAsync<OperationCanceledException>();
        stream.Length.Should().Be(0);
    }

    private static byte[] Frame(byte[] payload) =>
        new byte[] { 0x0B }.Concat(payload).Concat(new byte[] { 0x1C, 0x0D }).ToArray();

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4");
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDirectory, name));

    private sealed class FragmentedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class PendingReadStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
