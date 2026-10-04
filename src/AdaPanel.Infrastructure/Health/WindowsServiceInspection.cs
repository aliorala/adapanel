using System.Runtime.InteropServices;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Health;

internal static class WindowsServiceInspection
{
    internal static Task<Dictionary<string, string>> ReadAsync(IReadOnlyCollection<string> names,
        CancellationToken token, Func<string, string>? service = null, Func<string, string>? task = null) => Task.Run(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var status = (service ?? (name => WindowsNetworkHealth.ReadService(name).Status))(name);
                result[name] = status == "NotInstalled" ? (task ?? ReadTask)(name) : status;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // One unreadable service must not hide the rest or become "NotInstalled".
                result[name] = "Unknown";
            }
        }
        token.ThrowIfCancellationRequested();
        return result;
    }, token);

    private static string ReadTask(string name)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        object? folder = null, task = null;
        try
        {
            scheduler.Connect();
            folder = scheduler.GetFolder(@"\");
            try { task = ((dynamic)folder).GetTask(name); }
            // COM interop can translate ERROR_FILE_NOT_FOUND into FileNotFoundException.
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80070002))
            { return "NotInstalled"; }
            return TaskStatus((int)((dynamic)task).State);
        }
        finally { Release(task); Release(folder); Release(scheduler); }
    }

    internal static string TaskStatus(int state) => state switch
    {
        1 or 3 => "Stopped", 2 => "StartPending", 4 => "Running", _ => "Unknown"
    };

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
