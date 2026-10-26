using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class Argon2idOptionsValidator : IValidateOptions<Argon2idOptions>
{
    public ValidateOptionsResult Validate(string? name, Argon2idOptions options)
    {
        List<string> errors = [];
        if (options.MemorySizeKiB is < 19456 or > 262144)
            errors.Add("MemorySizeKiB must be between 19456 and 262144 KiB.");
        if (options.Iterations is < 2 or > 10)
            errors.Add("Iterations must be between 2 and 10.");
        if (options.Parallelism is < 1 or > 4)
            errors.Add("Parallelism must be between 1 and 4.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
