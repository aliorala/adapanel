using System.Reflection;
using System.Security.Claims;
using System.Text;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Files;
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

public sealed class FileEncodedWriteQuotaTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheQuotaUsesThePreservedEncodingAndOnlyItsActualNetGrowth(bool utf32, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-encoded-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var name = "quota-" + Guid.NewGuid().ToString("N") + ".test";
            var path = Path.Combine(root, "content.txt");
            var owner = Guid.NewGuid();
            Encoding codec = utf32 ? new UTF32Encoding(true, true, true) : new UnicodeEncoding(false, true, true);
            var before = new byte[] { }.Concat(codec.GetPreamble()).Concat(codec.GetBytes(new string('A', 100))).ToArray();
            await File.WriteAllBytesAsync(path, before);
            var content = new string('B', allowed ? 100 : utf32 ? 300_000 : 600_000);
            using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new PanelUser { Id = owner, UserName = "quota", Email = "quota@test.invalid", Role = UserRole.Customer,
                IsActive = true, PasswordHash = "original" });
            await db.SaveChangesAsync();
            var site = new SiteDto { Id = 31, Name = name, PhysicalPath = root, ApplicationRootPath = root, ApplicationPoolName = name };
            var sites = Stub<IIisSiteService>((method, _) => method == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(site) : throw new NotSupportedException(method));
            var scope = Stub<ICustomerScopeService>((method, _) => method switch
            {
                nameof(ICustomerScopeService.GetAssignedSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>([name]),
                nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
                nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope([name], 1, 1, 0, 0, 0)),
                nameof(ICustomerScopeService.GetDiskUsageMbAsync) => Task.FromResult(new DiskUsage(allowed ? 1 : 0, true, string.Empty)),
                nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(owner),
                _ => throw new NotSupportedException(method)
            });
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            using var gates = new FilePublicationAuthorityTests.LocalGate();
            using var services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var controller = new FilesController(new FileService(sites), scope, Stub<IAuditService>((_, _) => Task.CompletedTask), new ConcurrentWorkGate())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, "Customer"),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                    new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme)) } } };
            var error = await Record.ExceptionAsync(() => controller.WriteAsync(new WriteFileRequest { Path = $"sites/{name}/content.txt", Content = content }, default));
            if (allowed)
            {
                Assert.Null(error);
                Assert.Equal(codec.GetPreamble().Concat(codec.GetBytes(content)).ToArray(), await File.ReadAllBytesAsync(path));
            }
            else
            {
                Assert.Equal(before, await File.ReadAllBytesAsync(path));
                Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static T Stub<T>(Func<string, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
