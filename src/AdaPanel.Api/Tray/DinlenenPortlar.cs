using System.Runtime.InteropServices;

namespace AdaPanel.Api.Tray;

/// <summary>
/// Hangi sürecin hangi TCP portunu dinlediğini okur.
/// </summary>
/// <remarks>
/// <para>
/// Amaç kullanıcının şu sorusuna cevap vermek: <b>"MariaDB ve MySQL ikisi de kuruluysa hangisi
/// kullanılıyor?"</b> Cevap tahmin edilemez — bileşenin "varsayılan portu" diye bir liste yazmak
/// yanlış olurdu, çünkü port değiştirilebilir ve iki motor aynı anda kurulu olsa bile yalnız biri
/// portu tutabilir. Doğru cevap, o an gerçekten dinlenen porta bakmaktır.
/// </para>
/// <para>
/// .NET'in <c>IPGlobalProperties.GetActiveTcpListeners()</c> metodu portları verir ama HANGİ
/// SÜRECİN dinlediğini vermez; eşleştirme için Windows'un genişletilmiş TCP tablosu gerekiyor.
/// Yönetici yetkisi istemez.
/// </para>
/// </remarks>
public static class DinlenenPortlar
{
    private const int AF_INET = 2;
    private const uint TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int NO_ERROR = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>Süreç kimliği → dinlediği portlar (küçükten büyüğe, tekrarsız).</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<int>> SurecBasinaOku()
    {
        var sonuc = new Dictionary<int, List<int>>();

        var boyut = 0;
        var durum = GetExtendedTcpTable(IntPtr.Zero, ref boyut, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (durum != ERROR_INSUFFICIENT_BUFFER && durum != NO_ERROR)
        {
            return Bosalt(sonuc);
        }

        var tampon = Marshal.AllocHGlobal(boyut);
        try
        {
            durum = GetExtendedTcpTable(tampon, ref boyut, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (durum != NO_ERROR)
            {
                return Bosalt(sonuc);
            }

            var adet = Marshal.ReadInt32(tampon);
            var satirBoyu = Marshal.SizeOf<TcpSatiri>();
            var konum = tampon + 4;

            for (var i = 0; i < adet; i++)
            {
                var satir = Marshal.PtrToStructure<TcpSatiri>(konum);
                konum += satirBoyu;

                // Port ağ sırasında (big-endian) tutuluyor; iki bayt takas edilir.
                var port = ((satir.YerelPort & 0xFF) << 8) | ((satir.YerelPort >> 8) & 0xFF);
                if (port <= 0)
                {
                    continue;
                }

                if (!sonuc.TryGetValue(satir.SurecId, out var liste))
                {
                    liste = [];
                    sonuc[satir.SurecId] = liste;
                }

                if (!liste.Contains(port))
                {
                    liste.Add(port);
                }
            }
        }
        catch (Exception)
        {
            // Ölçemedik: boş döner ve arayüzde port hiç yazılmaz. Uydurma bir port yazmak,
            // kullanıcının yanlış servisi durdurmasına yol açabilirdi.
            return Bosalt(sonuc);
        }
        finally
        {
            Marshal.FreeHGlobal(tampon);
        }

        return Bosalt(sonuc);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> Bosalt(Dictionary<int, List<int>> kaynak)
        => kaynak.ToDictionary(
            oge => oge.Key,
            oge => (IReadOnlyList<int>)oge.Value.OrderBy(port => port).ToList());

    /// <summary>
    /// Windows'un <c>MIB_TCPROW_OWNER_PID</c> yapısı — ALTI alan.
    /// </summary>
    /// <remarks>
    /// Uzak adres ve uzak port alanları, yalnızca DİNLEYENLERİ isteyen sorguda da yapıda YER ALIR
    /// (değerleri sıfırdır). Önce dört alanlı yazılmıştı; bu durumda süreç kimliği uzak adres
    /// alanından okunuyor ve hiçbir port hiçbir servisle eşleşmiyordu — tepside port sütunu
    /// sessizce boş kalıyordu. Ölçüm yakaladı: WMI ve netstat portu gösteriyorken tepsi göstermiyordu.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpSatiri
    {
        public uint Durum;
        public uint YerelAdres;
        public int YerelPort;
        public uint UzakAdres;
        public int UzakPort;
        public int SurecId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExtendedTcpTable(
        IntPtr tablo,
        ref int boyut,
        bool sirali,
        int adresAilesi,
        uint sinif,
        int ayrilmis);
}
