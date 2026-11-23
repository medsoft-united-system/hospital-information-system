using System.Net;
using HospitalInformationSystem.Application.Models.Hl7Import;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.Api.Hl7.Transport;

public sealed class Hl7ImportOptionsValidator : IValidateOptions<Hl7ImportOptions>
{
    public ValidateOptionsResult Validate(string? name, Hl7ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var errors = new List<string>();
        if (!IPAddress.TryParse(options.ListenAddress, out _))
            errors.Add("Hl7Import:ListenAddress must be an IP address.");
        if (options.Port is < 1 or > 65535)
            errors.Add("Hl7Import:Port must be between 1 and 65535.");
        if (options.ServerCertificate is null)
            errors.Add("Hl7Import:ServerCertificate is required.");
        else
        {
            RequireFile(options.ServerCertificate.PfxPath, "Hl7Import:ServerCertificate:PfxPath", errors);
            RequireFile(options.ServerCertificate.PasswordFile, "Hl7Import:ServerCertificate:PasswordFile", errors);
        }
        if (options.ClientTrust is null)
            errors.Add("Hl7Import:ClientTrust is required.");
        else
            RequireFile(options.ClientTrust.CaCertificatePath, "Hl7Import:ClientTrust:CaCertificatePath", errors);
        if (options.Timeouts is null)
            errors.Add("Hl7Import:Timeouts is required.");
        else
        {
            Range(options.Timeouts.HandshakeSeconds, 1, 300, "Hl7Import:Timeouts:HandshakeSeconds", errors);
            Range(options.Timeouts.FrameReadSeconds, 1, 300, "Hl7Import:Timeouts:FrameReadSeconds", errors);
            Range(options.Timeouts.ProcessingSeconds, 1, 600, "Hl7Import:Timeouts:ProcessingSeconds", errors);
            Range(options.Timeouts.WriteSeconds, 1, 300, "Hl7Import:Timeouts:WriteSeconds", errors);
            Range(options.Timeouts.ShutdownSeconds, 1, 120, "Hl7Import:Timeouts:ShutdownSeconds", errors);
        }
        if (options.Limits is null)
            errors.Add("Hl7Import:Limits is required.");
        else
        {
            Range(options.Limits.MaxPayloadBytes, 1, Hl7ImportProtocol.MaxPayloadBytes,
                "Hl7Import:Limits:MaxPayloadBytes", errors);
            Range(options.Limits.MaxConnections, 1, 256, "Hl7Import:Limits:MaxConnections", errors);
            Range(options.Limits.Backlog, 1, 1024, "Hl7Import:Limits:Backlog", errors);
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void RequireFile(string? path, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            errors.Add(name + " must refer to an existing file.");
    }

    private static void Range(int value, int minimum, int maximum, string name, List<string> errors)
    {
        if (value < minimum || value > maximum)
            errors.Add(name + " is outside the supported range.");
    }
}
