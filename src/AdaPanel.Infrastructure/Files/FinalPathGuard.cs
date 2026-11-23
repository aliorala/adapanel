using System.Runtime.InteropServices;
using System.Text;
using AdaPanel.Core.Common;
using Microsoft.Win32.SafeHandles;

namespace AdaPanel.Infrastructure.Files;

/// <summary>
/// Açılmış bir dosya tutamacının GERÇEK (bağlantılar çözülmüş) yolunun sandbox içinde olduğunu doğrular.
/// </summary>
/// <remarks>
/// Yol denetimi ile <c>new FileStream(path)</c> arasında bir pencere vardır (TOCTOU): kiracı o anda
/// bir klasörü junction'la değiştirebilir. Tutamaç açıldıktan SONRA ölçülen yol yarışa kapalıdır,
/// çünkü tutamaç artık sabit bir nesneye bağlıdır. Ölçülemeyen tutamaç reddedilir.
/// </remarks>
internal static class FinalPathGuard
{
    private const uint FileShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Akışın son yolu sandbox'ın içinde değilse <c>files.path_outside_root</c> fırlatır.</summary>
    public static void EnsureInside(Stream stream, string sandboxRoot)
    {
        if (stream is not FileStream file)
        {
            return;
        }

        var final = FinalPath(file.SafeFileHandle);
        if (final is null || !IsInside(final, sandboxRoot))
        {
            throw new AppException("files.path_outside_root", 400, 400);
        }
    }

    internal static bool IsInside(string finalPath, string sandboxRoot)
    {
        if (PathContainment.IsRootOrInside(sandboxRoot, finalPath))
        {
            return true;
        }

        // Kök sürücü eşlemesi / SUBST / 8.3 adı yüzünden sözcüksel olarak farklı yazılmış olabilir;
        // kökün KENDİ gerçek yolu ile de karşılaştır.
        using var handle = CreateFileW(sandboxRoot, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var rootFinal = FinalPath(handle);
        return rootFinal is not null && PathContainment.IsRootOrInside(rootFinal, finalPath);
    }

    internal static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
