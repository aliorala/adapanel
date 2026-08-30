using System.Reflection;
using System.Runtime.InteropServices;

namespace AdaPanel.Api.Tray;

/// <summary>Bir zamanlanmış görev hakkında ÖLÇÜLEBİLEN gerçek.</summary>
/// <remarks>
/// <see cref="Olculemedi"/> ile <see cref="Kayitsiz"/> AYNI ŞEY DEĞİLDİR ve asla birleştirilmemeli:
/// biri "soramadık", diğeri "sorduk, yok". İkisini tek değere indirmek, tepsinin kurulu bir
/// bileşene "kurulu değil / yeniden kurun" demesine yol açan hatanın ta kendisiydi.
/// </remarks>
public enum GorevDurumu
{
    /// <summary>Task Scheduler'a sorulamadı (çoğunlukla yetkisiz oturum). "Yok" DEĞİL.</summary>
    Olculemedi,

    /// <summary>Sorulabildi ve görev gerçekten kayıtlı değil.</summary>
    Kayitsiz,

    /// <summary>Kayıtlı ama devre dışı bırakılmış; açılışta da başlamaz.</summary>
    DevreDisi,

    /// <summary>Kayıtlı, şu an çalışmıyor.</summary>
    Durdu,

    /// <summary>Kayıtlı ve çalışıyor.</summary>
    Calisiyor
}

/// <summary>
/// Windows Task Scheduler'ı OKUR (yazmaz). Panelin uzun ömürlü yardımcı süreçleri — posta
/// sunucusu (<c>AdaStalwart</c>) ve önbellek (<c>AdaMemcached</c>) — Windows servisi olarak
/// değil ONSTART zamanlanmış görev olarak kayıtlıdır; durumları buradan sorulur.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden COM, neden <c>schtasks.exe</c> değil:</b> bu makinede ölçüldü —
/// <c>schtasks /Query /TN AdaStalwart</c> yetkisiz oturumda <c>exit 1</c> veriyor, VAR OLMAYAN bir
/// görev adı da <c>exit 1</c> veriyor. Yani schtasks "erişemedim" ile "yok"u aynı koda indiriyor;
/// üstelik metin çıktısı YERELLEŞTİRİLMİŞ (Status sütunu Türkçe Windows'ta Türkçe). COM arayüzü
/// ise sayısal <c>TASK_STATE</c> döndürür ve erişim reddini ayrı bir HRESULT ile bildirir.
/// </para>
/// <para>
/// <b>Yetki gerçeği (ölçüldü, aynı makine):</b> yetkisiz oturumda <c>GetTask("AdaStalwart")</c>
/// → <c>0x80070005 E_ACCESSDENIED</c>; <c>%SystemRoot%\System32\Tasks</c> listelenemiyor;
/// <c>Win32_Process.ExecutablePath</c> SYSTEM süreçleri için BOŞ geliyor. Yani tepsi normal
/// kullanıcı olarak çalışırken görevin durumunu SORAMAZ. Cevap "ölçülemedi"dir — ve çağıran
/// bunu bir arıza olarak saymaz. Yetkili oturumda aynı çağrı <c>State=3</c> (Ready) döndü.
/// </para>
/// </remarks>
public static class ZamanlanmisGorevler
{
    // TASK_STATE (taskschd.h): 0 Unknown, 1 Disabled, 2 Queued, 3 Ready, 4 Running.
    private const int TaskStateDisabled = 1;
    private const int TaskStateQueued = 2;
    private const int TaskStateReady = 3;
    private const int TaskStateRunning = 4;

    // Task Scheduler "görev yok" derken dosya-bulunamadı HRESULT'ını kullanır; erişim reddi
    // AYRI koddur. Bu ikisini karıştırmak, kurulu bileşene "kayıtlı değil" demek olurdu.
    private const int HResultDosyaYok = unchecked((int)0x80070002);
    private const int HResultYolYok = unchecked((int)0x80070003);
    private const int HResultErisimYok = unchecked((int)0x80070005);

    /// <summary>Kök klasördeki görevin durumunu okur. Hiçbir koşulda fırlatmaz.</summary>
    public static GorevDurumu Oku(string gorevAdi)
    {
        if (string.IsNullOrWhiteSpace(gorevAdi))
        {
            return GorevDurumu.Olculemedi;
        }

        object? hizmet = null;
        object? klasor = null;
        object? gorev = null;
        try
        {
            var tip = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false);
            if (tip is null)
            {
                return GorevDurumu.Olculemedi;
            }

            hizmet = Activator.CreateInstance(tip);
            if (hizmet is null)
            {
                return GorevDurumu.Olculemedi;
            }

            // Connect'in dört parametresi de isteğe bağlı (yerel makine, geçerli kullanıcı);
            // IDispatch geç bağlamada atlanan parametre Type.Missing ile bildirilir.
            Cagir(hizmet, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);

            klasor = Cagir(hizmet, "GetFolder", BindingFlags.InvokeMethod, "\\");
            if (klasor is null)
            {
                return GorevDurumu.Olculemedi;
            }

            gorev = Cagir(klasor, "GetTask", BindingFlags.InvokeMethod, gorevAdi);
            if (gorev is null)
            {
                return GorevDurumu.Olculemedi;
            }

            var durum = Cagir(gorev, "State", BindingFlags.GetProperty);
            return DurumuCevir(durum is null ? null : Convert.ToInt32(durum));
        }
        catch (Exception hata)
        {
            // Karar TÜRE değil, HRESULT'a bakar. ÖLÇÜLDÜ: geç bağlamada "görev yok" hatası
            // COMException olarak DEĞİL, TargetInvocationException içinde sarılı bir
            // FileNotFoundException (0x80070002) olarak geliyor. Önce tür süzülüyordu ve bu
            // yüzden HER cevap "ölçülemedi"ye düşüyordu — koruma sessizce ölüydü.
            return HResultuCevir(KokHResult(hata));
        }
        finally
        {
            Birak(gorev);
            Birak(klasor);
            Birak(hizmet);
        }
    }

    /// <summary>TASK_STATE sayısını anlamına çevirir. Bilinmeyen kod uydurulmaz.</summary>
    internal static GorevDurumu DurumuCevir(int? taskState) => taskState switch
    {
        TaskStateRunning => GorevDurumu.Calisiyor,
        // Kuyruğa alınmış görev birazdan başlayacak; "durdu" demek yanıltıcı olurdu ama
        // "çalışıyor" da değil. En yakın doğru: henüz çalışmıyor.
        TaskStateQueued or TaskStateReady => GorevDurumu.Durdu,
        TaskStateDisabled => GorevDurumu.DevreDisi,
        _ => GorevDurumu.Olculemedi
    };

    /// <summary>HRESULT'ı ayırır: "yok" ile "soramadık" aynı cevaba İNMEZ.</summary>
    internal static GorevDurumu HResultuCevir(int hResult) => hResult switch
    {
        HResultDosyaYok or HResultYolYok => GorevDurumu.Kayitsiz,
        HResultErisimYok => GorevDurumu.Olculemedi,
        _ => GorevDurumu.Olculemedi
    };

    /// <summary>
    /// Sarmalanmış hatanın KÖK HRESULT'ı.
    /// </summary>
    /// <remarks>
    /// Geç bağlama her hatayı <see cref="TargetInvocationException"/> içine sarar ve CLR, tanıdığı
    /// HRESULT'ları <c>COMException</c> yerine özel türlere (ör. <c>FileNotFoundException</c>)
    /// çevirir. Tür üzerinden ayıklamak bu yüzden güvenilmez; HRESULT hepsinde korunur.
    /// </remarks>
    internal static int KokHResult(Exception hata)
    {
        var suanki = hata;
        while (suanki is TargetInvocationException && suanki.InnerException is not null)
        {
            suanki = suanki.InnerException;
        }

        return suanki.HResult;
    }

    private static object? Cagir(object hedef, string ad, BindingFlags bayrak, params object?[] parametreler) =>
        hedef.GetType().InvokeMember(ad, bayrak, null, hedef, parametreler);

    private static void Birak(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.ReleaseComObject(com);
            }
            catch
            {
                // Serbest bırakamamak ölçümü değiştirmez.
            }
        }
    }
}
