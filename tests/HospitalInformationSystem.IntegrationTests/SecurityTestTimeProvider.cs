namespace HospitalInformationSystem.IntegrationTests;

public sealed class SecurityTestTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
