namespace HospitalInformationSystem.Infrastructure.Maintenance;

public sealed record PasswordHashingTimingSummary(
    IReadOnlyList<double> SamplesMilliseconds,
    double MedianMilliseconds,
    double P95Milliseconds);
