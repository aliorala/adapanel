using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>MailEnable's documented SMTP command-file queue. Message bodies are never returned.</summary>
internal sealed class MailEnableQueue(Func<string?> rootResolver)
{
    internal static string? InstalledRoot()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = registry.OpenSubKey(@"SOFTWARE\Mail Enable\Mail Enable\Connectors");
        return key?.GetValue("Connector Root Directory") is string root ? Path.Combine(root, "SMTP", "Outgoing") : null;
    }

    private string Root()
    {
        var root = rootResolver();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new AppException("mail.queue_unavailable", 503, 503);
        root = Path.GetFullPath(root);
        RejectLinks(root);
        return root;
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
             item is not null; item = item is DirectoryInfo dir ? dir.Parent : ((FileInfo)item).Directory)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AppException("mail.queue_unsafe_path", 409, 409);
    }

    private static string FileName(string id)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,128}\.MAI\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new AppException("mail.queue_invalid_id", 400, 400);
        return id;
    }

    public MailQueueDto Read(int limit)
    {
        var root = Root();
        var files = Directory.GetFiles(root, "*.MAI").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var messages = new List<MailQueueMessageDto>();
        foreach (var file in files.Take(Math.Clamp(limit, 1, 500)))
        {
            try { var item = ReadOne(root, Path.GetFileName(file)); if (item is not null) messages.Add(item); }
            catch (FileNotFoundException) { /* Delivery can consume a command between enumeration and read. */ }
        }
        return new() { Installed = true, Configured = true, TotalCount = files.Length, Messages = messages };
    }

    public MailQueueMessageDto? ReadOne(string id) => ReadOne(Root(), FileName(id));

    private static MailQueueMessageDto? ReadOne(string root, string id)
    {
        var command = Path.Combine(root, FileName(id));
        RejectLinks(command);
        if (!File.Exists(command)) return null;
        using var stream = new FileStream(command, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 1024 * 1024) throw new AppException("mail.queue_invalid_command", 502, 502);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var message = Parse(id, reader.ReadToEnd(), TimeZoneInfo.Local);
        var body = Path.Combine(root, "Messages", id);
        RejectLinks(body);
        message.SizeBytes = File.Exists(body) ? new FileInfo(body).Length : 0;
        return message;
    }

    internal static MailQueueMessageDto Parse(string id, string text, TimeZoneInfo timeZone)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index > 0) fields[line[..index].Trim()] = line[(index + 1)..].TrimEnd('\r');
        }
        string Get(string key) => fields.GetValueOrDefault(key, "");
        if (string.IsNullOrWhiteSpace(Get("Status")) || string.IsNullOrWhiteSpace(Get("Recipients")))
            throw new AppException("mail.queue_invalid_command", 502, 502);
        DateTimeOffset? Time(string key)
        {
            if (!long.TryParse(Get(key), out var seconds) || seconds <= 0 || seconds > 253402300799) return null;
            try
            {
                var local = DateTime.SpecifyKind(new DateTime(1970, 1, 1).AddSeconds(seconds), DateTimeKind.Unspecified);
                return timeZone.IsInvalidTime(local) ? null : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
            }
            catch (ArgumentException) { return null; }
        }
        static string Address(string value) => value.StartsWith("[SMTP:", StringComparison.OrdinalIgnoreCase) && value.EndsWith(']') ? value[6..^1] : value;
        var retry = Time("NextSendTime");
        return new()
        {
            Id = id, ReturnPath = Address(Get("Sender")), ReceivedFromIp = Get("ClientIP"),
            CreatedAtUtc = Time("TimeAcquired") ?? Time("TimeAquired"), NextRetryUtc = retry,
            Recipients = Get("Recipients").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(recipient => new MailQueueRecipientDto
                {
                    Address = Address(recipient), Status = Get("Status"), StatusDetail = Get("Status"),
                    RetryCount = int.TryParse(Get("Retries"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                    RetryDueUtc = retry, QueueName = "SMTP/Outgoing"
                }).ToArray()
        };
    }

    public bool Delete(string id)
    {
        var root = Root();
        var command = Path.Combine(root, FileName(id));
        var body = Path.Combine(root, "Messages", id);
        RejectLinks(command); RejectLinks(body);
        if (!File.Exists(command)) return false;
        try
        {
            // Deny concurrent connector reads/writes while deleting both queue files.
            using var commandLock = new FileStream(command, FileMode.Open, FileAccess.Read, FileShare.Delete);
            using var reader = new StreamReader(commandLock, leaveOpen: true);
            if (commandLock.Length > 1024 * 1024) throw new AppException("mail.queue_invalid_command", 502, 502);
            var message = Parse(id, reader.ReadToEnd(), TimeZoneInfo.Local);
            if (message.Recipients.Any(r => r.Status.Equals("Sending", StringComparison.OrdinalIgnoreCase)))
                throw new AppException("mail.queue_message_busy", 409, 409);
            using var bodyLock = File.Exists(body) ? new FileStream(body, FileMode.Open, FileAccess.Read, FileShare.Delete) : null;
            File.Delete(command);
            if (bodyLock is not null) File.Delete(body);
            return true;
        }
        catch (IOException) { throw new AppException("mail.queue_message_busy", 409, 409); }
    }
}
