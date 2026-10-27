namespace HospitalInformationSystem.Infrastructure.Maintenance;

public sealed record PasswordHashingBenchmarkResult(
    DateTimeOffset MeasuredAtUtc,
    string Runtime,
    string OperatingSystem,
    string ProcessArchitecture,
    int ProcessorCount,
    int Iterations,
    int MemorySizeKiB,
    int AlgorithmIterations,
    int Parallelism,
    PasswordHashingTimingSummary Hash,
    PasswordHashingTimingSummary Verify);
