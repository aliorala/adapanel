using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Diagnostics;

// Independent from licensing and error reports. The preference controls details; daily version reporting continues.
public sealed class InstallationReportingService(JsonFileStore store, IHttpClientFactory clients)
{
    public const string ClientName = "AdaPanel.InstallationReporting";
    public const string Endpoint = "https://adapanel.host/installation-stats/heartbeat.php";
    internal const string StatePath = "config/installation-reporting.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(store.ResolvePath(StatePath), _ => new(1, 1));
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<InstallationReportingDto> GetAsync(CancellationToken ct)
    {
        var state = await ReadStateAsync(ct);
        return View(state);
    }

    public async Task<InstallationReportingDto> UpdateAsync(UpdateInstallationReportingRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await ReadStateAsync(ct);
            if (string.IsNullOrWhiteSpace(request.Revision) || request.Revision != state.Revision)
                throw new AppException("installationReporting.changed", 409, 409);
            state.Enabled = request.Enabled;
            state.ConsentAtUtc = request.Enabled ? state.ConsentAtUtc ?? Clock() : null;
            if (request.Enabled && state.Token.Length == 0)
                state.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            state.Revision = Guid.NewGuid().ToString("N");
            await store.WriteAsync(StatePath, state, ct);
            // Use the already-held gate: preference saving and the worker cannot race,
            // and changing the preference never resets the delivery schedule.
            await ReportLockedAsync(state, ct);
            return View(state);
        }
        finally { gate.Release(); }
    }

    public async Task ReportIfDueAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await ReadStateAsync(ct);
            await ReportLockedAsync(state, ct);
        }
        finally { gate.Release(); }
    }

    private async Task ReportLockedAsync(State state, CancellationToken ct)
    {
            var now = Clock();
            if (NextAttempt(state) is { } next && now < next) return;
            if (state.Token.Length == 0)
                state.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (state.Token.Length != 64 || !state.Token.All(Uri.IsHexDigit))
            {
                state.LastError = "invalid_identity";
                await store.WriteAsync(StatePath, state, ct);
                return;
            }
            // Reserve a bounded retry before connecting. Process termination or request
            // cancellation must not permit a tight retry loop after restarting.
            state.ConsecutiveFailures = Math.Min(Math.Clamp(state.ConsecutiveFailures, 0, RetryDelays.Length) + 1, RetryDelays.Length);
            state.LastAttemptAtUtc = now;
            state.NextAttemptAtUtc = now + RetryDelays[state.ConsecutiveFailures - 1];
            state.LastError = "pending";
            await store.WriteAsync(StatePath, state, ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var client = clients.CreateClient(ClientName);
                client.MaxResponseContentBufferSize = 4096;
                var version = VersionInfo.Current.Split('+')[0];
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 2, installationToken = state.Token, panelVersion = version,
                    shareDetails = state.Enabled
                });
                // IIS/PHP paths may stall on streaming JSON with chunked framing.
                // Buffer this small, bounded report so Content-Length is known before sending.
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                content.Headers.ContentLength = payload.LongLength;
                using var response = await client.PostAsync(Endpoint, content, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    state.LastError = $"http_{(int)response.StatusCode}";
                else
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    if (body.RootElement.ValueKind == JsonValueKind.Object
                        && body.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True
                        && body.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number
                        && schema.TryGetInt32(out var value) && value == 2)
                    {
                        state.LastAcceptedAtUtc = Clock();
                        state.NextAttemptAtUtc = state.LastAcceptedAtUtc + TimeSpan.FromHours(24);
                        state.ConsecutiveFailures = 0;
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

    private static DateTimeOffset? NextAttempt(State state)
    {
        var next = state.NextAttemptAtUtc;
        // Upgrade old states without losing their last attempt or daily success limit.
        if (next is null && state.LastAttemptAtUtc is { } attempt)
            next = attempt + RetryDelays[0];
        if (state.LastAcceptedAtUtc is { } accepted && (next is null || next < accepted + TimeSpan.FromHours(24)))
            next = accepted + TimeSpan.FromHours(24);
        return next;
    }

    private async Task<State> ReadStateAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync<State?>(StatePath, null, ct);
        if (state is not null) return state;
        // Only a missing file is a fresh installation. Existing null/empty state must
        // never turn an explicit previous preference into enabled reporting.
        if (File.Exists(store.ResolvePath(StatePath)))
            throw new JsonException("Invalid installation reporting state.");
        return new State { Enabled = true };
    }

    private static InstallationReportingDto View(State state) => new()
    {
        Enabled = state.Enabled,
        ConsentAtUtc = state.ConsentAtUtc, LastAttemptAtUtc = state.LastAttemptAtUtc,
        LastAcceptedAtUtc = state.LastAcceptedAtUtc, NextAttemptAtUtc = NextAttempt(state),
        LastError = state.LastError, Revision = state.Revision
    };

    internal sealed class State
    {
        public bool Enabled { get; set; }
        public string Token { get; set; } = "";
        public DateTimeOffset? ConsentAtUtc { get; set; }
        public DateTimeOffset? LastAttemptAtUtc { get; set; }
        public DateTimeOffset? LastAcceptedAtUtc { get; set; }
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string LastError { get; set; } = "";
        public string Revision { get; set; } = "initial";
    }
}
