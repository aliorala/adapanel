using System.Reflection;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PanelSettingsDnsSyncTests
{
    [Fact]
    public async Task Same_address_save_without_explicit_general_settings_flag_does_not_reconcile()
    {
        var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "176.43.253.97" }));
        var dns = Stub<IDnsService>((method, _) => {
            Assert.Equal("GetServerAddressDnsSyncAsync", method.Name);
            return Task.FromResult<ServerAddressDnsSyncDto?>(null);
        });
        await Controller(preferences, dns).UpdateAsync(new UpdatePanelPreferencesRequest { ServerAddress = "176.43.253.97" }, default);
    }
    [Theory]
    [InlineData("176.43.253.97")]
    [InlineData("https://176.43.253.97:8888/")]
    public async Task Save_changed_ipv4_migrates_after_preferences_and_keeps_existing_save_contract(string requested)
    {
        var saved = false;
        var called = false;
        var preferences = Stub<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
            ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "84.51.22.239" })
            : Save());
        Task<PanelPreferencesDto> Save() { saved = true; return Task.FromResult(new PanelPreferencesDto { ServerAddress = "176.43.253.97" }); }
        var dns = Stub<IDnsService>((method, args) => {
            if (method.Name == "GetServerAddressDnsSyncAsync") return Task.FromResult<ServerAddressDnsSyncDto?>(null);
            if (method.Name == "PrepareServerAddressDnsSyncAsync") { Assert.False(saved); return Task.CompletedTask; }
            Assert.True(saved); Assert.Equal("84.51.22.239", args![0]); Assert.Equal("176.43.253.97", args[1]);
            called = true; return Task.FromResult(new ServerAddressDnsSyncDto());
        });
        await Controller(preferences, dns).UpdateAsync(new UpdatePanelPreferencesRequest { ServerAddress = requested }, default);
        Assert.True(called);
    }

    [Fact]
    public async Task Rejected_preferences_save_cancels_only_uncommitted_preparation()
    {
        var cancelled = false;
        var preferences = Stub<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
            ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "84.51.22.239" })
            : throw new InvalidOperationException("invalid backup path"));
        var dns = Stub<IDnsService>((method, _) => method.Name == "GetServerAddressDnsSyncAsync"
            ? Task.FromResult<ServerAddressDnsSyncDto?>(null) : Action(method));
        Task Action(MethodInfo method) { if (method.Name == "CancelPreparedServerAddressDnsSyncAsync") cancelled = true; return Task.CompletedTask; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller(preferences, dns).UpdateAsync(new UpdatePanelPreferencesRequest { ServerAddress = "176.43.253.97" }, default));
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Same_ip_save_retries_persisted_failure_instead_of_erasing_pending_work()
    {
        var attempts = 0;
        var pending = new ServerAddressDnsSyncDto { OldAddress = "84.51.22.239", NewAddress = "176.43.253.97", Pending = true };
        var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "176.43.253.97" }));
        var dns = Stub<IDnsService>((method, args) => {
            if (method.Name == "GetServerAddressDnsSyncAsync") return Task.FromResult<ServerAddressDnsSyncDto?>(pending);
            attempts++; Assert.Equal(pending.OldAddress, args![0]); return Task.FromResult(pending);
        });
        await Controller(preferences, dns).UpdateAsync(new UpdatePanelPreferencesRequest { ServerAddress = "176.43.253.97" }, default);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("mail.example.test")]
    [InlineData("2001:db8::1")]
    [InlineData("")]
    [InlineData("192.168.1.80")]
    public async Task Hostname_IPv6_blank_and_private_address_saves_do_not_trigger_ipv4_migration(string next)
    {
        var preferences = Stub<IPanelPreferencesService>((method, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = method.Name == "GetAsync" ? "84.51.22.239" : next }));
        var dns = Stub<IDnsService>((method, _) => {
            Assert.Equal("GetServerAddressDnsSyncAsync", method.Name);
            return Task.FromResult<ServerAddressDnsSyncDto?>(null);
        });
        await Controller(preferences, dns).UpdateAsync(new UpdatePanelPreferencesRequest { ServerAddress = next }, default);
    }

    private static PanelSettingsController Controller(IPanelPreferencesService preferences, IDnsService dns)
        => new(preferences, Stub<IAuditService>((_, _) => Task.CompletedTask), dns)
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
