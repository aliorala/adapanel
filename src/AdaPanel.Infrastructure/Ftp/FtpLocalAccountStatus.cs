using System.Runtime.InteropServices;

namespace AdaPanel.Infrastructure.Ftp;

/// <summary>Local SAM lookup; a process exit code or localized message cannot prove absence.</summary>
internal static class FtpLocalAccountStatus
{
    internal const uint Success = 0;
    internal const uint UserNotFound = 2221;
    private const uint InvalidData = 13;

    internal delegate uint Query(string? server, string userName, uint level, out IntPtr buffer);

    // https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netusergetinfo
    // Null server means this machine, level zero requests only USER_INFO_0 (the account name).
    // Both disabled and enabled accounts exist; this lookup does not infer their login policy.
    internal static uint Read(string userName, CancellationToken token, Query? query = null,
        Func<IntPtr, uint>? release = null)
    {
        token.ThrowIfCancellationRequested();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var status = (query ?? NetUserGetInfo)(null, userName, 0, out buffer);
            token.ThrowIfCancellationRequested();
            return status == Success && buffer == IntPtr.Zero ? InvalidData : status;
        }
        finally
        {
            // The API owns allocation, including any buffer returned alongside a failure.
            // https://learn.microsoft.com/en-us/windows/win32/api/lmapibuf/nf-lmapibuf-netapibufferfree
            if (buffer != IntPtr.Zero) (release ?? NetApiBufferFree)(buffer);
        }
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetUserGetInfo(string? serverName, string userName, uint level, out IntPtr buffer);

    [DllImport("Netapi32.dll", ExactSpelling = true)]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}
