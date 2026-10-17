using System.Text.Json;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    [Theory]
    [InlineData("Renewal succeeded", 1, false)]
    [InlineData(" [INFO] Renewal for \"[IIS] example.test\" succeeded\r\n", 1, false)]
    [InlineData("Renewal for \"example.test\" succeeded with errors", 1, true)]
    [InlineData("Renewal for \"successful.test\" failed, will retry on next run", 0, false)]
    [InlineData("Renewal unsuccessful", 0, false)]
    [InlineData("Renewal for \"example.test\" not successful", 0, false)]
    [InlineData("Renewal for \"example.test\" failed\nA separate step succeeded", 0, false)]
    [InlineData("Renewal for \"a.test\" succeeded\r\nRenewal for \"b.test\" succeeded with errors", 2, true)]
    public async Task SslRenewalCountsOnlyCompletedOutcomeLinesAndRetainsReportedErrors(string output, int count, bool errors)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        f.SslNativeResult = new CommandResult { ExitCode = 0, StandardOutput = output };
        var result = await f.SslNativeService.RenewAllAsync(f.Token);
        Assert.True(result.Measured);
        Assert.Equal(count, result.RenewedCount);
        Assert.Equal(errors, !string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Contains(output.Trim(), result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SslRenewalReportedErrorsInEitherStreamPreventACompletedAutomationStatus(bool stderr)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        const string outcome = "Renewal for \"example.test\" succeeded with errors";
        f.SslNativeResult = new CommandResult { ExitCode = 0,
            StandardOutput = stderr ? "Checking renewals" : outcome,
            StandardError = stderr ? outcome : "" };
        await f.InvokeSsl();
        var status = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("completed_with_errors", status.LastSslStatus);
        Assert.Contains("win-acme", status.LastSslDetails);
        Assert.Equal(1, f.SslCommands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task SslResponseKeepsBothCommandStreamsIncludingTimeoutDiagnosis(int exitCode)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        f.SslNativeResult = new CommandResult
        {
            ExitCode = exitCode,
            StandardOutput = "Renewal succeeded\nChecking remaining renewals.",
            StandardError = "Process timed out after 600 s; termination was requested.\n" + RenewalDiagnostic
        };
        var result = await f.SslNativeService.RenewAllAsync(f.Token);
        Assert.Equal(exitCode == 0, result.Measured);
        Assert.Equal(1, result.RenewedCount);
        Assert.Contains("Checking remaining renewals.", result.Output);
        Assert.Contains("Process timed out after 600 s", result.Output);
        Assert.Contains(RenewalDiagnosis, result.Output);
        AssertNoRenewalSecrets(JsonSerializer.Serialize(result));
        Assert.Equal(1, f.SslCommands);
    }

    [Fact]
    public async Task SslResponseDoesNotCountADiagnosticCopyOfTheSuccessLineTwice()
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        f.SslNativeResult = new CommandResult
        {
            ExitCode = 1,
            StandardOutput = "Renewal succeeded",
            StandardError = "Renewal succeeded\n" + RenewalDiagnostic
        };
        var result = await f.SslNativeService.RenewAllAsync(f.Token);
        Assert.False(result.Measured);
        Assert.Equal(1, result.RenewedCount);
        Assert.Contains(RenewalDiagnosis, result.Output);
        AssertNoRenewalSecrets(JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SslResponseMasksActualRenewalCommandOutputWithoutLosingItsMeasurement(bool successful, bool stdout)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        var output = "Renewal succeeded\n" + RenewalDiagnostic;
        f.SslNativeResult = new CommandResult
        {
            ExitCode = successful ? 0 : 1,
            StandardOutput = stdout ? output : "",
            StandardError = stdout ? "" : output
        };
        var result = await f.SslNativeService.RenewAllAsync(f.Token);
        Assert.Equal(successful, result.Measured);
        Assert.Equal(1, result.RenewedCount);
        Assert.Contains("Renewal succeeded", result.Output);
        Assert.Contains(RenewalDiagnosis, result.Output);
        Assert.Contains("acme.example.test/directory", result.Output);
        AssertNoRenewalSecrets(result.Output);
        AssertNoRenewalSecrets(JsonSerializer.Serialize(result));
        Assert.Equal(1, f.SslCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SslResponseMasksFailureAndOutputAtEveryAssignment(bool deserialize)
    {
        var result = deserialize
            ? JsonSerializer.Deserialize<RenewAllResultDto>(JsonSerializer.Serialize(new { FailureReason = RenewalDiagnostic, Output = RenewalDiagnostic }))!
            : new RenewAllResultDto { FailureReason = RenewalDiagnostic, Output = RenewalDiagnostic };
        AssertNoRenewalSecrets(result.FailureReason);
        AssertNoRenewalSecrets(result.Output);
        Assert.Contains(RenewalDiagnosis, result.FailureReason);
        result.Output = RenewalDiagnostic;
        result.FailureReason = RenewalDiagnostic;
        AssertNoRenewalSecrets(JsonSerializer.Serialize(result));
        result.Output = "Renewal succeeded; certificate expires in 60 days.";
        result.FailureReason = "";
        Assert.Equal("Renewal succeeded; certificate expires in 60 days.", result.Output);
        Assert.Equal("", result.FailureReason);
    }
}
