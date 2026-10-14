using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Tests;

// Older publication fixtures model panel intent and record commands, not live inventory.
// Explicitly model a missing native zone there. Live authoritative inventory is covered
// separately by DnsLiveAuthorityTests with actual PowerShell record objects.
internal static class DnsTestInventory
{
    internal static Task<CommandResult> MissingZone(string script, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"Exists\":false,\"ZoneType\":\"\",\"Records\":[]}" });
    }
}
