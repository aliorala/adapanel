using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileUploadQuotaTests
{
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 50, true)]
    [InlineData(100, 101, false)]
    [InlineData(0, 1, false)]
    public async Task Dolu_kotada_yalniz_buyuyen_yukleme_reddedilir(long existing, int incoming, bool allowed)
    {
        var name = "quota-" + Guid.NewGuid().ToString("N") + ".test";
        var root = Path.Combine(Path.GetTempPath(), "adapanel-upload-quota-" + Guid.NewGuid().ToString("N"));
        var target = $"sites/{name}/güncel.txt";
        var owner = Guid.NewGuid();
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PanelUser { Id = owner, UserName = "quota", Email = "quota@example.test", Role = UserRole.Customer,
            IsActive = true, PasswordHash = "fixture-hash" });
        await db.SaveChangesAsync();
        var site = new SiteDto { Id = 10, Name = name, PhysicalPath = root, ApplicationRootPath = root, ApplicationPoolName = name };
        var sites = Stub<IIisSiteService>((method, _) => method == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(site) : throw new NotSupportedException(method));
        using var gates = new FilePublicationAuthorityTests.LocalGate();
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
            .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
        var measured = new List<string>();
        var saved = false;
        var files = Stub<IFileService>((method, args) => method switch
        {
            nameof(IFileService.SaveUploadAsync) => Save((string)args![0]!, (long)args[2]!,
                (Func<SiteDto, CancellationToken, Task>)args[4]!,
                (Func<long, CancellationToken, Task>)args[5]!, (CancellationToken)args[6]!),
            _ => throw new NotSupportedException(method)
        });
        async Task<string> Save(string path, long expectedLength, Func<SiteDto, CancellationToken, Task> approve,
            Func<long, CancellationToken, Task> beforeWrite, CancellationToken token)
        {
            await approve(site, token);
            measured.Add(path);
            Assert.Equal(incoming, expectedLength);
            await beforeWrite(Math.Max(0, expectedLength - existing), token);
            await approve(site, token);
            Assert.Equal(target, path); saved = true; return path;
        }
        var scopes = Stub<ICustomerScopeService>((method, _) => method switch
        {
            nameof(ICustomerScopeService.GetAssignedSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>([name]),
            nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope([name], 1, 1, 0, 0, 0)),
            nameof(ICustomerScopeService.GetDiskUsageMbAsync) => Task.FromResult(new DiskUsage(1, true, string.Empty)),
            nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(owner),
            _ => throw new NotSupportedException(method)
        });
        var audit = Stub<IAuditService>((method, _) => method == nameof(IAuditService.WriteForSubscriptionAsync)
            ? Task.CompletedTask : throw new NotSupportedException(method));
        var controller = new FilesController(files, scopes, audit, new ConcurrentWorkGate())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, "Customer"),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("fixture-hash")),
                    new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())
                ], CookieAuthenticationDefaults.AuthenticationScheme))
            } }
        };
        using var content = new MemoryStream(new byte[incoming]);
        var upload = new FormFile(content, 0, incoming, "file", "güncel.txt");
        var error = await Record.ExceptionAsync(() => controller.UploadAsync($"sites/{name}", upload, CancellationToken.None));
        if (allowed) Assert.Null(error);
        else Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(allowed, saved);
        Assert.Equal([target], measured);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static T Stub<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Handler = handler;
        return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
