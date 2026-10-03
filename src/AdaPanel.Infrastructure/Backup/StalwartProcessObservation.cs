namespace AdaPanel.Infrastructure.Backup;

internal enum StalwartProcessState { Stopped, Running, Unknown }

internal sealed record StalwartProcessObservation(StalwartProcessState State, string Detail = "");

internal sealed record StalwartProcessSample(int Id, bool Exited, string? ExecutablePath, string Error = "");

internal static class StalwartProcessProbe
{
    internal static bool? BelongsToInstallation(string installDirectory, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)) return null;
        try
        {
            var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var image = Path.GetFullPath(executablePath);
            return image.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(image).Equals("stalwart.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { return null; }
    }

    internal static StalwartProcessObservation Classify(string installDirectory, IEnumerable<StalwartProcessSample> samples)
    {
        var unknown = new List<string>();
        foreach (var sample in samples)
        {
            if (sample.Exited) continue;
            var match = BelongsToInstallation(installDirectory, sample.ExecutablePath);
            if (match == true) return new(StalwartProcessState.Running, $"pid {sample.Id}");
            if (match is null) unknown.Add($"pid {sample.Id}: {sample.Error}".TrimEnd());
        }
        return unknown.Count == 0 ? new(StalwartProcessState.Stopped, "No running Stalwart process from this installation was observed.")
            : new(StalwartProcessState.Unknown, string.Join("; ", unknown));
    }

    internal static StalwartProcessObservation Observe(string installDirectory, CancellationToken cancellationToken)
    {
        var samples = new List<StalwartProcessSample>();
        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcessesByName("stalwart"); }
        catch (Exception exception) { return new(StalwartProcessState.Unknown, exception.Message); }
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited) continue;
                    var image = process.MainModule?.FileName;
                    // The process may exit while its module is being read.
                    if (!process.HasExited) samples.Add(new(process.Id, false, image));
                }
                catch (InvalidOperationException) { /* The process exited during observation. */ }
                catch (Exception exception) { samples.Add(new(process.Id, false, null, exception.Message)); }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return Classify(installDirectory, samples);
    }
}
