using System.Globalization;
using System.Text.Json;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;

namespace HospitalInformationSystem.Maintenance;

internal static class PasswordHashingBenchmarkCommand
{
    public static async Task<int> RunAsync(string[] arguments, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var options = configuration.GetSection(Argon2idOptions.SectionName).Get<Argon2idOptions>() ?? new Argon2idOptions();
        var samples = 5;
        string? outputPath = null;
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length) throw new ArgumentException("Для каждого параметра измерения требуется значение.");
            var name = arguments[index];
            var value = arguments[index + 1];
            if (name == "--output") { outputPath = value; continue; }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"Параметр {name} должен быть целым положительным числом.");
            switch (name)
            {
                case "--iterations": samples = number; break;
                case "--memory-kib": options.MemorySizeKiB = number; break;
                case "--algorithm-iterations": options.Iterations = number; break;
                case "--parallelism": options.Parallelism = number; break;
                default: throw new ArgumentException($"Неизвестный параметр измерения: {name}.");
            }
        }
        var result = PasswordHashingBenchmark.Run(options, samples, cancellationToken);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(json);
        else
        {
            await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, cancellationToken);
            Console.WriteLine("Измерение KDF сохранено. Пароль и хеш в отчёт не включаются.");
        }
        return 0;
    }
}
