using System.Text.Json;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Maintenance;
using HospitalInformationSystem.Maintenance;
using HospitalInformationSystem.Models.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

if (args.Length == 0 || args[0] != "benchmark-password-hashing" &&
    (args.Length > 2 || args[0] is not ("report" or "apply") || args[0] == "apply" && args.Length != 2))
{
    Console.Error.WriteLine("Из корня репозитория: dotnet run --project src/Presentation/HospitalInformationSystem.Maintenance -- report [report.json] | apply plan.json");
    Console.Error.WriteLine("Измерение KDF без БД: benchmark-password-hashing [--iterations 5] [--output benchmark.json] [--memory-kib 65536] [--algorithm-iterations 3] [--parallelism 1]");
    return 2;
}

try
{
    var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("src/Presentation/HospitalInformationSystem.Api/appsettings.json", optional: true)
        .AddJsonFile("src/Presentation/HospitalInformationSystem.Api/appsettings.Local.json", optional: true)
        .AddEnvironmentVariables().Build();
    if (args[0] == "benchmark-password-hashing")
        return await PasswordHashingBenchmarkCommand.RunAsync(args[1..], configuration, CancellationToken.None);
    var connectionString = configuration.GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Задайте ConnectionStrings__Postgres или локальный appsettings.Local.json API. Запускайте команду из корня репозитория.");
    var options = new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql(connectionString).Options;
    await using var db = new HospitalDbContext(options);
    var service = new SecurityUpgradeService(db, TimeProvider.System);
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    if (args[0] == "apply")
    {
        var plan = JsonSerializer.Deserialize<SecurityUpgradePlan>(await File.ReadAllTextAsync(args[1]), jsonOptions)
            ?? throw new ArgumentException("Пустой план перехода.");
        await service.ApplyAsync(plan, CancellationToken.None);
        Console.WriteLine("План применён. Предметные данные и UUID сохранены. Готовность входа проверяется отдельно через первоначальную настройку и TOTP.");
    }
    else
    {
        var json = JsonSerializer.Serialize(await service.ReportAsync(CancellationToken.None), jsonOptions);
        if (args.Length == 2) await File.WriteAllTextAsync(args[1], json + Environment.NewLine);
        else Console.WriteLine(json);
    }
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or JsonException or OptionsValidationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Npgsql.NpgsqlException)
{
    Console.Error.WriteLine("Операция БД не выполнена. Проверьте соединение и отчёт совместимости. Подготовленные переименования фиксируются до миграций; повторное применение того же плана безопасно.");
    return 1;
}
