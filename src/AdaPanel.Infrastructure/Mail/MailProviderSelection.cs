using System.Diagnostics;
using AdaPanel.Core.Common;
using Microsoft.Extensions.Configuration;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>Selects management only; never starts, stops or migrates a mail server.</summary>
public sealed class MailProviderSelection(IConfiguration configuration)
{
    public string Current => Resolve(configuration["Mail:Provider"], MailService.IsInstalled(),
        MailEnableMailService.IsInstalled(), IsRunning("stalwart"), IsRunning("MESMTPC"));

    internal static string Resolve(string? configured, bool stalwartInstalled, bool mailEnableInstalled,
        bool stalwartRunning, bool mailEnableRunning)
    {
        var provider = (configured ?? "auto").Trim().ToLowerInvariant();
        if (provider is not ("auto" or "stalwart" or "mailenable"))
            throw new AppException("mail.provider_invalid", 409, 409);
        if (stalwartRunning && mailEnableRunning)
            throw new AppException("mail.provider_conflict", 409, 409);
        if (provider != "auto")
        {
            if (provider == "stalwart" && (!stalwartInstalled || mailEnableRunning) ||
                provider == "mailenable" && (!mailEnableInstalled || stalwartRunning))
                throw new AppException("mail.provider_conflict", 409, 409);
            return provider;
        }
        if (mailEnableRunning) return "mailenable";
        if (stalwartRunning) return "stalwart";
        if (stalwartInstalled && mailEnableInstalled)
            throw new AppException("mail.provider_ambiguous", 409, 409);
        return mailEnableInstalled ? "mailenable" : "stalwart";
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
