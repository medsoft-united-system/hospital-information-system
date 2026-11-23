using HospitalInformationSystem.Models.Hl7Import;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public sealed class Hl7ImportOptions
{
    public const string SectionName = "Hl7Import";

    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Hl7ImportProtocol.DefaultMllpPort;
    public Hl7ServerCertificateOptions ServerCertificate { get; set; } = new();
    public Hl7ClientTrustOptions ClientTrust { get; set; } = new();
    public Hl7TimeoutOptions Timeouts { get; set; } = new();
    public Hl7LimitOptions Limits { get; set; } = new();
}

public sealed class Hl7ServerCertificateOptions
{
    public string PfxPath { get; set; } = "";
    public string PasswordFile { get; set; } = "";
}

public sealed class Hl7ClientTrustOptions
{
    public string CaCertificatePath { get; set; } = "";
}

public sealed class Hl7TimeoutOptions
{
    public int HandshakeSeconds { get; set; } = 10;
    public int FrameReadSeconds { get; set; } = 30;
    public int ProcessingSeconds { get; set; } = 30;
    public int WriteSeconds { get; set; } = 10;
    public int ShutdownSeconds { get; set; } = 10;
}

public sealed class Hl7LimitOptions
{
    public int MaxPayloadBytes { get; set; } = Hl7ImportProtocol.MaxPayloadBytes;
    public int MaxConnections { get; set; } = 16;
    public int Backlog { get; set; } = 32;
}
