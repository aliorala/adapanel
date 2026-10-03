using System.Text.Json;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
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
