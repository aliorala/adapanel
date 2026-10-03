using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        const string target = "sites/a.test/güncel.txt";
        var owner = Guid.NewGuid();
        var measured = new List<string>();
        var saved = false;
        var files = Stub<IFileService>((method, args) => method switch
        {
            nameof(IFileService.MeasureAsync) => Measure((string[])args![0]!),
            nameof(IFileService.SaveUploadAsync) => Save((string)args![0]!),
            _ => throw new NotSupportedException(method)
        });
        Task<DirectorySizeDto> Measure(string[] paths)
        {
            measured.AddRange(paths);
            return Task.FromResult(new DirectorySizeDto { TotalBytes = existing });
        }
        Task<string> Save(string path)
        {
            Assert.Equal(target, path); saved = true; return Task.FromResult(path);
        }
        var scopes = Stub<ICustomerScopeService>((method, _) => method switch
        {
            nameof(ICustomerScopeService.GetAssignedSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>(["a.test"]),
            nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope(["a.test"], 1, 1, 0, 0, 0)),
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
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, "Customer")
                ], "test"))
            } }
        };
        using var content = new MemoryStream(new byte[incoming]);
        var upload = new FormFile(content, 0, incoming, "file", "güncel.txt");
        var error = await Record.ExceptionAsync(() => controller.UploadAsync("sites/a.test", upload, CancellationToken.None));
        if (allowed) Assert.Null(error);
        else Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(allowed, saved);
        Assert.Equal([target], measured);
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
