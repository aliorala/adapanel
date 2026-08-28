using System;
using System.Globalization;
using System.Text;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>Modülün bir istek için ne yapacağı.</summary>
internal enum HtaccessMode
{
    /// <summary>Hiçbir şey yapma. İstek DOKUNULMADAN geçer. Bilinmeyen/bozuk her durumun karşılığı.</summary>
    Off = 0,

    /// <summary>
    /// Değerlendir ama UYGULAMA — yalnız günlüğe yaz. KADEMELİ AÇILIŞIN can damarı:
    /// yönetici gerçek trafikte ".htaccess açılsaydı ne olurdu"yu isteklerin hiçbirini bozmadan görür.
    /// </summary>
    Observe = 1,

    /// <summary>Değerlendir ve UYGULA.</summary>
    Enforce = 2
}

/// <summary>
/// Anahtar dosyasının biçimi ve ayrıştırıcısı.
///
/// NEDEN JSON DEĞİL, düz metin: bu dosya ACİL DURUM KURTARMA ARACIDIR. Üç gerekçe:
/// 1) Modül net48'de çalışıyor; net48'in çekirdek kütüphanesinde System.Text.Json YOK. JSON için
///    modüle NuGet bağımlılığı eklemek, her müşteri sitesinin sürecine ikinci bir DLL sokmak demekti.
/// 2) Bir ayrıştırıcı ne kadar basitse o kadar az patlar. Burada patlayacak bir şey neredeyse yok;
///    yine de her hata "Off"a düşüyor.
/// 3) Sunucu yangınındayken yönetici bu dosyayı Not Defteri ile açıp tek satır değiştirebilmeli.
///    JSON'da unutulan bir virgül, kurtarma anında modülü kapatmak yerine ayrıştırmayı bozardı.
/// </summary>
internal static class HtaccessSwitchFile
{
    internal const string EnabledKey = "enabled";
    internal const string ModeKey = "mode";

    /// <summary>
    /// Metni moda çevirir. HİÇBİR KOŞULDA istisna fırlatmaz — çağıran taraf zaten fail-open ama
    /// bu katman da kendi başına güvenli olmalı (savunma katmanları birbirine güvenmez).
    /// </summary>
    internal static HtaccessMode Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            // BOŞ DOSYA = KAPALI. Yarım yazılmış (disk doldu, süreç öldü) bir dosya da buraya düşer.
            return HtaccessMode.Off;
        }

        var enabled = false;
        var mode = HtaccessMode.Enforce;

        // net48'de string.Split(char, StringSplitOptions) aşırı yüklemesi YOK -> char[] kullanılıyor.
        var lines = content!.Split(new[] { '\n' }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim().ToLowerInvariant();
            var value = line.Substring(separator + 1).Trim();

            if (string.Equals(key, EnabledKey, StringComparison.Ordinal))
            {
                enabled =
                    string.Equals(value, "1", StringComparison.Ordinal) ||
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(key, ModeKey, StringComparison.Ordinal))
            {
                if (string.Equals(value, "observe", StringComparison.OrdinalIgnoreCase))
                {
                    mode = HtaccessMode.Observe;
                }
                else if (string.Equals(value, "enforce", StringComparison.OrdinalIgnoreCase))
                {
                    mode = HtaccessMode.Enforce;
                }
                else
                {
                    // TANINMAYAN mod = KAPALI. "mode=enfroce" yazım hatası SESSİZCE "enforce" sayılsaydı,
                    // kullanıcı gözlem modunda sandığı sitede gerçek uygulama başlatmış olurdu.
                    return HtaccessMode.Off;
                }
            }
        }

        return enabled ? mode : HtaccessMode.Off;
    }

    /// <summary>Anahtar dosyasının içeriğini üretir (panel tarafı yazar, modül tarafı okur).</summary>
    internal static string Render(HtaccessMode mode, string? note)
    {
        var builder = new StringBuilder();
        builder.Append("# AdaPanel .htaccess modul anahtari\n");
        builder.Append("# Bu dosyayi SILMEK ya da enabled=0 yapmak modulu ANINDA kapatir.\n");
        builder.Append("# Yazan: AdaPanel paneli. Elle duzenlenebilir (kurtarma icin kasitli olarak boyle).\n");
        builder.Append("# Yazilma zamani (UTC): ");
        builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        builder.Append('\n');

        if (!string.IsNullOrWhiteSpace(note))
        {
            // Not, YORUM satırı olarak yazılır -> içindeki hiçbir şey ayrıştırmayı etkileyemez.
            // Satır sonları temizlenmezse kullanıcı notu sahte bir "enabled=1" satırı ENJEKTE edebilirdi.
            var singleLine = note!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            builder.Append("# Not: ");
            builder.Append(singleLine);
            builder.Append('\n');
        }

        // SIRA ÖNEMLİ: önce mode, SONRA enabled. Ölçüldü — ters sırada, yarım yazılmış bir dosya
        // (disk doldu, süreç öldü, dosya elle düzenlenirken kesildi) "enabled=1" satırında bitebilir
        // ve <see cref="Parse"/> mod satırını görmediği için varsayılan ENFORCE'a düşerdi: gözlem
        // moduna almak istenen sunucu, yarım bir dosya yüzünden GERÇEK UYGULAMAYA geçerdi. Bu sıra
        // ile her yarım dosya en fazla "mode=..." satırını içerir, "enabled" olmadığı için KAPALI
        // sayılır. Panelin yazımı zaten atomiktir (HtaccessSwitchStore.WriteAtomic); bu, o korumanın
        // ARKASINDAKİ ikinci katman — dosya bir kurtarma aracı olduğu için elle de düzenleniyor.
        builder.Append(ModeKey);
        builder.Append('=');
        builder.Append(mode == HtaccessMode.Observe ? "observe" : "enforce");
        builder.Append('\n');

        builder.Append(EnabledKey);
        builder.Append('=');
        builder.Append(mode == HtaccessMode.Off ? '0' : '1');
        builder.Append('\n');

        return builder.ToString();
    }
}
