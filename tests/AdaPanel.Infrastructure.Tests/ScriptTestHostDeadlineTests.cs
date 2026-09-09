using System.Diagnostics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("Script host timing")]
public sealed class ScriptTestHostDeadlineTests
{
    [Fact]
    public void SynchronousCaller_DoesNotNeedItsSynchronizationContextToResume()
    {
        var previous = SynchronizationContext.Current;
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var result = ScriptTestHost.Run("Start-Sleep -Milliseconds 100; Write-Output 'completed'", 15_000);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("completed", result.StdOut);
            Assert.Equal(0, context.Posts);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    [Fact]
    public void InfiniteScript_IsStoppedWithinDeadline()
    {
        var elapsed = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => ScriptTestHost.Run("while ($true) { Start-Sleep -Milliseconds 100 }", 1500));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "A blocked output reader must not bypass the process deadline.");
    }

    [Fact]
    public void BothOutputPipes_AreDrainedWithoutDeadlock()
    {
        var result = ScriptTestHost.Run("for ($i = 0; $i -lt 3000; $i++) { [Console]::Error.WriteLine(('e' * 120)); [Console]::Out.WriteLine(('o' * 120)) }", 30_000);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StdErr.Length > 300_000);
        Assert.True(result.StdOut.Length > 300_000);
    }
}

// Process deadline checks run without thousands of other tests competing for process startup.
[CollectionDefinition("Script host timing", DisableParallelization = true)]
public sealed class ScriptHostTimingCollection
{
}
