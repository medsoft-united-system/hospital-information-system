using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HospitalInformationSystem.Application.Models.Maintenance;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.Maintenance;

public static class PasswordHashingBenchmark
{
    public static PasswordHashingBenchmarkResult Run(Argon2idOptions options, int iterations,
        CancellationToken cancellationToken)
    {
        if (iterations is < 3 or > 50) throw new ArgumentOutOfRangeException(nameof(iterations), "Use 3–50 samples.");
        var hasher = new Argon2idPasswordHasher(Options.Create(options));
        var user = new HospitalUser();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        cancellationToken.ThrowIfCancellationRequested();
        var warmup = hasher.HashPassword(user, password);
        RequireVerified(hasher.VerifyHashedPassword(user, warmup, password));
        var hashSamples = new double[iterations];
        var verifySamples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var hash = hasher.HashPassword(user, password);
            hashSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            started = Stopwatch.GetTimestamp();
            var verified = hasher.VerifyHashedPassword(user, hash, password);
            verifySamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            RequireVerified(verified);
        }
        return new PasswordHashingBenchmarkResult(DateTimeOffset.UtcNow, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            iterations, options.MemorySizeKiB, options.Iterations, options.Parallelism,
            Summarize(hashSamples), Summarize(verifySamples));
    }

    private static PasswordHashingTimingSummary Summarize(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new PasswordHashingTimingSummary(samples, median, sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1]);
    }

    private static void RequireVerified(PasswordVerificationResult result)
    {
        if (result != PasswordVerificationResult.Success)
            throw new InvalidOperationException("Проверка синтетического пароля при измерении KDF не прошла.");
    }
}
