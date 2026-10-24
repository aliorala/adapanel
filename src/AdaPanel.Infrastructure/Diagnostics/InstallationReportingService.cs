using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Diagnostics;

// Independent from licensing and error reports. Disabled installations never create an ID or send.
public sealed class InstallationReportingService(JsonFileStore store, IHttpClientFactory clients)
{
    public const string ClientName = "AdaPanel.InstallationReporting";
    public const string Endpoint = "https://adapanel.host/installation-stats/heartbeat.php";
    internal const string StatePath = "config/installation-reporting.json";
    private readonly SemaphoreSlim gate = new(1, 1);
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<InstallationReportingDto> GetAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(StatePath, new State(), ct);
        return View(state);
    }

    public async Task<InstallationReportingDto> UpdateAsync(UpdateInstallationReportingRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(StatePath, new State(), ct);
            if (string.IsNullOrWhiteSpace(request.Revision) || request.Revision != state.Revision)
                throw new AppException("installationReporting.changed", 409, 409);
            if ((request.Location?.Length ?? 0) > 100 || (request.Datacenter?.Length ?? 0) > 100)
                throw new AppException("installationReporting.invalid", 400, 400);
            state.Enabled = request.Enabled;
            state.Location = request.Location?.Trim() ?? "";
            state.Datacenter = request.Datacenter?.Trim() ?? "";
            state.ConsentAtUtc = request.Enabled ? state.ConsentAtUtc ?? Clock() : null;
            if (request.Enabled && state.Token.Length == 0)
                state.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            state.Revision = Guid.NewGuid().ToString("N");
            await store.WriteAsync(StatePath, state, ct);
            return View(state);
        }
        finally { gate.Release(); }
    }

    public async Task ReportIfDueAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(StatePath, new State(), ct);
            var now = Clock();
            if (!state.Enabled || state.ConsentAtUtc is null || state.Token.Length != 64) return;
            // Persist before connecting: restarts, failures and concurrent ticks cannot increase frequency.
            if (state.LastAttemptAtUtc is { } last && now - last < TimeSpan.FromHours(24)) return;
            state.LastAttemptAtUtc = now;
            state.LastError = "pending";
            await store.WriteAsync(StatePath, state, ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var client = clients.CreateClient(ClientName);
                client.MaxResponseContentBufferSize = 4096;
                var version = VersionInfo.Current.Split('+')[0];
                using var response = await client.PostAsJsonAsync(Endpoint, new
                {
                    schemaVersion = 1, installationToken = state.Token, panelVersion = version,
                    location = state.Location, datacenter = state.Datacenter
                }, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    state.LastError = $"http_{(int)response.StatusCode}";
                else
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    if (body.RootElement.ValueKind == JsonValueKind.Object
                        && body.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True
                        && body.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number
                        && schema.TryGetInt32(out var value) && value == 1)
                    {
                        state.LastAcceptedAtUtc = Clock();
                        state.LastError = "";
                    }
                    else state.LastError = "invalid_response";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { state.LastError = "timeout"; }
            catch (HttpRequestException) { state.LastError = "network"; }
            catch (JsonException) { state.LastError = "invalid_response"; }
            // No response body, token, IP or user-entered fields go to logs.
            await store.WriteAsync(StatePath, state, ct);
        }
        finally { gate.Release(); }
    }

    private static InstallationReportingDto View(State state) => new()
    {
        Enabled = state.Enabled, Location = state.Location, Datacenter = state.Datacenter,
        ConsentAtUtc = state.ConsentAtUtc, LastAttemptAtUtc = state.LastAttemptAtUtc,
        LastAcceptedAtUtc = state.LastAcceptedAtUtc, LastError = state.LastError, Revision = state.Revision
    };

    internal sealed class State
    {
        public bool Enabled { get; set; }
        public string Token { get; set; } = "";
        public string Location { get; set; } = "";
        public string Datacenter { get; set; } = "";
        public DateTimeOffset? ConsentAtUtc { get; set; }
        public DateTimeOffset? LastAttemptAtUtc { get; set; }
        public DateTimeOffset? LastAcceptedAtUtc { get; set; }
        public string LastError { get; set; } = "";
        public string Revision { get; set; } = "initial";
    }
}
