namespace HospitalInformationSystem.Infrastructure.Identity;

public sealed class Argon2idOptions
{
    public const string SectionName = "Security:PasswordHashing:Argon2id";
    public int MemorySizeKiB { get; set; } = 65536;
    public int Iterations { get; set; } = 3;
    public int Parallelism { get; set; } = 1;
}
