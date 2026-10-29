using AdaPanel.Core.Dtos.Notifications;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NotificationCredentialIdentityTests
{
    private static UpdateNotificationSettingsRequest Request() => new()
    { SmtpHost = "smtp.example.net", SmtpPort = 587, SmtpUserName = "smtp-user", FromEmail = "from@example.net", ToEmail = "to@example.net", UseSsl = true };

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("user")]
    [InlineData("tls")]
    public async Task BlankSecretDoesNotMigrateToChangedTransportIdentity(string field)
    {
        using var f = new NotificationLifecycleTests.Fixture(); await f.Seed();
        var request = Request();
        if (field == "host") request.SmtpHost = "new.example.net";
        if (field == "port") request.SmtpPort = 465;
        if (field == "user") request.SmtpUserName = "other-user";
        if (field == "tls") request.UseSsl = false;
        var result = await f.Service().UpdateSettingsAsync(request);
        Assert.False(result.HasSmtpPassword); Assert.Empty((await f.Stored()).SmtpPassword);
        Assert.Equal(0, f.Smtp.Connections); Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Http.Requests);
        // An explicit later send to the fake transport cannot obtain the old credential.
        await f.Service().SendEmailAsync("fixture", "body");
        Assert.NotEqual("old-secret", f.Smtp.Password);
    }

    [Fact]
    public async Task NormalizedSameIdentityKeepsLatestSecret()
    {
        using var f = new NotificationLifecycleTests.Fixture(); await f.Seed();
        var request = Request(); request.SmtpHost = " SMTP.EXAMPLE.NET "; request.SmtpUserName = " smtp-user ";
        var result = await f.Service().UpdateSettingsAsync(request);
        Assert.True(result.HasSmtpPassword); Assert.Equal("protected:old-secret", (await f.Stored()).SmtpPassword);
        Assert.Equal(0, f.ConnectorCalls);
    }

    [Fact]
    public async Task NewIdentityUsesOnlyExplicitNewSecret()
    {
        using var f = new NotificationLifecycleTests.Fixture(); await f.Seed();
        var request = Request(); request.SmtpHost = "new.example.net"; request.SmtpPassword = "new-secret";
        await f.Service().UpdateSettingsAsync(request);
        await f.Service().SendEmailAsync("fixture", "body");
        Assert.Equal("new-secret", f.Smtp.Password); Assert.Equal("new.example.net", f.Smtp.Host);
    }

    [Fact]
    public async Task StaleBlankSubmissionCannotCarryAnotherIdentitysConcurrentRotation()
    {
        using var f = new NotificationLifecycleTests.Fixture(); await f.Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = f.Service(beforeWrite: async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        var saving = stale.UpdateSettingsAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var latest = Request(); latest.SmtpHost = "new.example.net"; latest.SmtpPassword = "new-secret";
            await f.Service().UpdateSettingsAsync(latest);
        }
        finally { release.TrySetResult(); }
        await saving;
        var stored = await f.Stored(); Assert.Equal("smtp.example.net", stored.SmtpHost); Assert.Empty(stored.SmtpPassword);
        Assert.Equal(0, f.ConnectorCalls); Assert.Equal(0, f.Smtp.Connections);
    }
}
