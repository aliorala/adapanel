using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ErrorReportAuditPrivacyTests
{
    [Theory]
    [InlineData("https://reports.example.test/ingest/opaqueCollectorAccess359594", "opaqueCollectorAccess359594")]
    [InlineData("https://reports.example.test/ingest?customAccess=opaqueCollectorAccess359594", "opaqueCollectorAccess359594")]
    [InlineData("https://account:opaqueCollectorAccess359594@reports.example.test/ingest", "opaqueCollectorAccess359594")]
    public async Task SavingCollectorKeepsCredentialsOutOfAuditWithoutChangingTheConfiguredTarget(string url, string credential)
    {
        var sender = new Sender();
        var audit = new RecordingAuditService();
        var controller = Controller(sender, audit);
        var result = await controller.UpdateSettingsAsync(new ErrorReportingSettingsDto
        {
            Enabled = true, CollectorUrl = url, RetentionDays = 30
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(url, sender.Saved!.CollectorUrl);
        var record = Assert.Single(audit.Kayitlar);
        Assert.Equal("error_reports.settings_updated", record.Action);
        // The common audit writer's generic redactor alone cannot recognize arbitrary
        // collector credentials in a path or a provider-specific query parameter.
        var storedDetails = AuditSecretRedactor.SanitizeDetails(record.Details);
        Assert.DoesNotContain(credential, storedDetails);
        Assert.Contains("reports.example.test", storedDetails);
        Assert.Contains("retention 30", storedDetails);
    }

    [Theory]
    [InlineData("", "not configured")]
    [InlineData("https://reports.example.test/", "https://reports.example.test/")]
    public async Task SavingCollectorRetainsUsefulNonSecretAuditContext(string url, string expected)
    {
        var audit = new RecordingAuditService();
        await Controller(new Sender(), audit).UpdateSettingsAsync(new ErrorReportingSettingsDto
        {
            Enabled = false, CollectorUrl = url, RetentionDays = 7
        }, CancellationToken.None);
        var record = Assert.Single(audit.Kayitlar);
        Assert.Contains(expected, record.Details);
        Assert.Contains("disabled", record.Details);
        Assert.Contains("retention 7", record.Details);
    }

    private static ErrorReportsController Controller(Sender sender, RecordingAuditService audit) => new(null!, sender, audit)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"))
            }
        }
    };

    private sealed class Sender : IErrorReportSender
    {
        internal ErrorReportingSettingsDto? Saved;
        public Task<ErrorReportingSettingsDto> UpdateSettingsAsync(ErrorReportingSettingsDto settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.FromResult(settings);
        }
        public Task<ErrorReportingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> SendPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
