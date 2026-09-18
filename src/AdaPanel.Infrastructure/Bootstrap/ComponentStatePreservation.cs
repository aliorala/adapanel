using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.SiteBuilder;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>Kurulum dizini yenilenirken kullanıcı verisi ve yapılandırmasını korur.</summary>
internal sealed class ComponentStatePreservation
{
    private readonly string root;
    private readonly List<(string Saved, string Target, bool Directory)> entries = [];

    private ComponentStatePreservation(string root) => this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static ComponentStatePreservation Capture(string root, IEnumerable<string> relativePaths)
    {
        var state = new ComponentStatePreservation(root);
        var paths = relativePaths.Select(relative => Path.GetFullPath(Path.Combine(state.root, relative))).ToArray();
        // Bütün hedefleri taşımadan önce doğrula; bir bozuk yol diğerlerini yerinden etmesin.
        foreach (var path in paths) state.Check(path);
        try
        {
            foreach (var path in paths)
            {
                var directory = Directory.Exists(path);
                if (!directory && !File.Exists(path)) continue;
                var saved = Path.Combine(Path.GetDirectoryName(state.root)!,
                    $".{Path.GetFileName(state.root)}-preserved-{Guid.NewGuid():N}");
                if (directory) Directory.Move(path, saved);
                else File.Move(path, saved);
                state.entries.Add((saved, path, directory));
            }
            return state;
        }
        catch
        {
            state.Restore();
            throw;
        }
    }

    internal void Restore()
    {
        List<Exception> errors = [];
        foreach (var entry in entries.ToArray())
        {
            try
            {
                Check(entry.Target);
                SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(entry.Saved)!, entry.Saved);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
                // Paket aynı yolda varsayılan dosya/dizin açabilir. Kullanıcının durumu esastır;
                // paket kopyasını da silmeden kenara al. Özellikle config/ içindeki .dist dosyaları
                // eski kullanıcı ayarı yerine geçmemeli.
                if (Directory.Exists(entry.Target)) Directory.Move(entry.Target, entry.Saved + ".package");
                else if (File.Exists(entry.Target)) File.Move(entry.Target, entry.Saved + ".package");
                if (entry.Directory) Directory.Move(entry.Saved, entry.Target);
                else File.Move(entry.Saved, entry.Target);
                entries.Remove(entry);
            }
            catch (Exception exception)
            {
                errors.Add(new IOException($"Preserved state could not be restored. Recovery path: {entry.Saved}", exception));
            }
        }
        // Sessizce devam etmek postInstall'ın boş veritabanı üretmesine neden olabilir.
        if (errors.Count > 0) throw new AggregateException("Component state restoration failed; preserved paths must not be deleted.", errors);
    }

    private void Check(string path)
    {
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AppException("software.data_preserve_failed", 500, 500, "Preservation path is outside the installation directory.");
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, path);
    }
}
