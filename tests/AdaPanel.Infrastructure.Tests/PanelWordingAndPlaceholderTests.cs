using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Arayüz METİNLERİNİN nöbetçisi: ölü çeviri anahtarı, kuralsız etiket ve panelin kendi kurduğu
/// motorda ÇALIŞAMAYACAK örnek değer bırakmaz.
/// <para>
/// Bu üç bulgunun ortak yanı: hiçbiri istisna fırlatmıyor, hiçbiri günlüğe düşmüyor. Yalnızca
/// kullanıcıyı yanıltıyorlar. Bu yüzden tek yakalanma yolları böyle bir testtir.
/// </para>
/// </summary>
public sealed class PanelWordingAndPlaceholderTests
{
    /// <summary>
    /// TR/EN anahtar kümesi BİREBİR aynı olmalı. Bu tur 12 ölü anahtar silindiği için ayrıca
    /// önemli: silme iki dosyadan birinde unutulursa panel diğer dilde ham anahtar basar.
    /// </summary>
    [Fact]
    public void Turkce_Ve_Ingilizce_Sozlukler_Birebir_Ayni_Anahtarlara_Sahip()
    {
        var (tr, en) = Sozlukler();

        Assert.Empty(tr.Keys.Except(en.Keys, StringComparer.Ordinal));
        Assert.Empty(en.Keys.Except(tr.Keys, StringComparer.Ordinal));
        Assert.True(tr.Count > 4000, $"Sözlük taraması bozuk olabilir: {tr.Count} anahtar bulundu.");
    }

    /// <summary>
    /// ÖLÜ ANAHTAR NÖBETÇİSİ. Hiçbir yerde kullanılmayan çeviri, bakımı yapılan ama hiç görünmeyen
    /// metindir; üstelik bayat kopyalar gerçek anahtarla karışır (silinen
    /// <c>mail.unsubscribe_requested</c>, arka ucun kullandığı <c>mail.list.*</c> ile karışıyordu).
    /// <para>
    /// DİNAMİK BİRLEŞTİRME YANLIŞ POZİTİF VERİR: <c>t(`panel.processes.category.${x}`)</c> ve C#
    /// tarafındaki <c>$"panel.siteBuilder.template.{id}"</c> gibi yerler anahtarı ÇALIŞMA ANINDA
    /// kurar. Tarayıcı bu önekleri toplayıp muaf tutar. İlk ölçümde bu kural olmadan 22 "yetim"
    /// çıkmıştı; 11'i site kurucunun C# tarafından üretilen gerçek anahtarlarıydı.
    /// </para>
    /// </summary>
    [Fact]
    public void Kullanilmayan_Ceviri_Anahtari_Kalmadi()
    {
        var (tr, _) = Sozlukler();
        var kok = BulKok();
        var metinler = KaynakMetinleri(kok);
        var birlesik = string.Join("\n", metinler);
        var onekler = DinamikOnekler(metinler);

        var yetimler = tr.Keys
            .Where(anahtar => !birlesik.Contains(anahtar, StringComparison.Ordinal))
            .Where(anahtar => !onekler.Any(onek => anahtar.StartsWith(onek, StringComparison.Ordinal)))
            .OrderBy(anahtar => anahtar, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            yetimler.Length == 0,
            "Hiçbir yerde kullanılmayan çeviri anahtarları var:\n  " + string.Join("\n  ", yetimler));
    }

    /// <summary>
    /// YIKICI EYLEM ETİKETİ KURALI (bu turda kondu, öncesinde kuralsız karışıktı):
    /// <list type="bullet">
    /// <item><b>Sil / Delete</b> — nesne ve içindeki VERİ kalıcı olarak yok olur.</item>
    /// <item><b>Kaldır / Remove</b> — kurulu ya da bağlı bir şey sistemden çıkarılır; veri kaybı
    /// amaçlanmaz, yeniden kurulabilir/eklenebilir (PHP sürümü, yazılım bileşeni, önbellek motoru,
    /// nginx, takma ad, marka logosu, küme düğümü).</item>
    /// </list>
    /// Kural, İKİ DİLİN AYNI ŞEYİ SÖYLEMESİ olarak ölçülür: TR "Sil" diyorsa EN "Delete", TR
    /// "Kaldır" diyorsa EN "Remove" demeli. Ölçüldüğünde tek ihlal <c>panel.docker.remove</c>
    /// idi (TR "Sil" ⟷ EN "Remove"); konteyner/imaj silme yazılabilir katmanı yok ettiği için
    /// "Sil/Delete" tarafına alındı.
    /// </summary>
    [Fact]
    public void Yikici_Eylem_Etiketleri_Iki_Dilde_Ayni_Kurali_Izler()
    {
        var (tr, en) = Sozlukler();

        var ihlaller = new List<string>();
        foreach (var (anahtar, turkce) in tr)
        {
            if (!en.TryGetValue(anahtar, out var ingilizce))
            {
                continue;
            }

            var trDeger = turkce.Trim();
            var enDeger = ingilizce.Trim();

            if (trDeger == "Sil" && !string.Equals(enDeger, "Delete", StringComparison.Ordinal))
            {
                ihlaller.Add($"{anahtar}: tr=\"Sil\" ama en=\"{enDeger}\" (beklenen \"Delete\")");
            }

            if (trDeger == "Kaldır" && !string.Equals(enDeger, "Remove", StringComparison.Ordinal))
            {
                ihlaller.Add($"{anahtar}: tr=\"Kaldır\" ama en=\"{enDeger}\" (beklenen \"Remove\")");
            }

            if (enDeger == "Delete" && trDeger != "Sil")
            {
                ihlaller.Add($"{anahtar}: en=\"Delete\" ama tr=\"{trDeger}\" (beklenen \"Sil\")");
            }

            if (enDeger == "Remove" && trDeger != "Kaldır")
            {
                ihlaller.Add($"{anahtar}: en=\"Remove\" ama tr=\"{trDeger}\" (beklenen \"Kaldır\")");
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Yıkıcı eylem etiketleri kuralı bozulmuş:\n  " + string.Join("\n  ", ihlaller));

        // Nöbetçi gerçekten bir şeye bakıyor mu: her iki etiket ailesi de dolu olmalı.
        Assert.True(tr.Values.Count(deger => deger.Trim() == "Sil") >= 10);
        Assert.True(tr.Values.Count(deger => deger.Trim() == "Kaldır") >= 5);
    }

    /// <summary>
    /// ARIZA ile AYAR aynı kelimeyle anlatılamaz.
    /// <para>
    /// <c>panel.firewall.ports.status.problem</c> "Kapalı" yazıyordu; panelin "bilinçle kapatılmış"
    /// için kullandığı <c>panel.status.disabled</c> de "Kapalı". Oysa güvenlik duvarındaki bu
    /// durum GEREKLİ bir portun dinlenmediği ya da geçirilmediği anlamına gelir — yani gerçek bir
    /// arıza. Kullanıcı onu bir ayar sanıp görmezden gelebiliyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Guvenlik_Duvari_Arizasi_Bilincli_Kapatma_Ile_Ayni_Kelimeyi_Kullanmaz()
    {
        var (tr, en) = Sozlukler();

        Assert.NotEqual(tr["panel.status.disabled"], tr["panel.firewall.ports.status.problem"]);
        Assert.NotEqual(en["panel.status.disabled"], en["panel.firewall.ports.status.problem"]);

        // "Kapalı" ailesinin tamamıyla çakışmamalı: aynı kelimenin başka bir kaynağı da yanıltır.
        var kapaliDegerler = tr
            .Where(çift => çift.Key != "panel.firewall.ports.status.problem")
            .Where(çift => çift.Value.Trim() == "Kapalı")
            .Select(çift => çift.Key)
            .ToArray();

        Assert.DoesNotContain(tr["panel.firewall.ports.status.problem"].Trim(), new[] { "Kapalı" });
        Assert.NotEmpty(kapaliDegerler);
    }

    /// <summary>
    /// DOCKER ÖRNEK İMAJI, PANELİN KURDUĞU MOTORDA ÇALIŞABİLMELİ.
    /// <para>
    /// <c>components.json</c> → <c>docker-engine</c> resmi STATIC Windows binary'sini kurar ve
    /// Windows "Containers" özelliğini açar (bkz. <c>installDockerEngine</c>). Yani motor WINDOWS
    /// konteynerleri çalıştırır. Ekrandaki yer tutucu <c>nginx:latest</c> ise yalnız linux/amd64
    /// yayımlanır: kullanıcının gördüğü İLK örnek "no matching manifest for windows/amd64" ile
    /// garanti hata verirdi.
    /// </para>
    /// <para>
    /// CANLI DOĞRULANMADI: bu makinede Docker kurulu değil; bulgu bileşen tanımı ile ekran metni
    /// arasındaki çelişkiye dayanıyor. Test de tam olarak o çelişkiyi ölçer.
    /// </para>
    /// </summary>
    [Fact]
    public void Docker_Ekraninin_Ornek_Imajlari_Windows_Konteyneri()
    {
        var kok = BulKok();
        var bilesenler = File.ReadAllText(Path.Combine(kok, "components.json"));
        var ekran = File.ReadAllText(Path.Combine(kok, "frontend", "src", "views", "DockerView.vue"));

        // Ön koşul: motor gerçekten Windows container motoru mu? Değilse bu testin gerekçesi düşer.
        Assert.Contains("docker-engine", bilesenler, StringComparison.Ordinal);
        Assert.Contains("Containers", bilesenler, StringComparison.Ordinal);

        var yerTutucular = Regex.Matches(ekran, @"placeholder=""([^""]+)""")
            .Select(eslesme => eslesme.Groups[1].Value)
            .ToArray();

        var imajYerTutuculari = yerTutucular
            .Where(deger => deger.Contains(':') && !deger.Contains(' ') && !deger.Contains('='))
            .ToArray();

        Assert.NotEmpty(imajYerTutuculari);
        foreach (var deger in imajYerTutuculari)
        {
            Assert.StartsWith("mcr.microsoft.com/windows/", deger, StringComparison.Ordinal);
        }

        // Linux-only imaj adları örnek olarak GERİ GELMEMELİ.
        foreach (var linuxImaji in new[] { "nginx:latest", "redis:latest", "postgres:latest", "ubuntu:latest" })
        {
            Assert.DoesNotContain($"placeholder=\"{linuxImaji}\"", ekran, StringComparison.Ordinal);
        }
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private static (IReadOnlyDictionary<string, string> Tr, IReadOnlyDictionary<string, string> En) Sozlukler()
    {
        var kok = BulKok();
        return (Oku(Path.Combine(kok, "frontend", "src", "i18n", "tr.json")),
                Oku(Path.Combine(kok, "frontend", "src", "i18n", "en.json")));

        static IReadOnlyDictionary<string, string> Oku(string yol) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(yol))
            ?? throw new InvalidOperationException($"Sözlük okunamadı: {yol}");
    }

    /// <summary>Anahtarın geçebileceği TÜM kaynak metinleri (sözlük dosyalarının kendisi hariç).</summary>
    private static IReadOnlyList<string> KaynakMetinleri(string kok)
    {
        var uzantilar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vue", ".ts", ".js", ".cs", ".json", ".html", ".ps1", ".php"
        };

        var klasorler = new[] { "frontend/src", "src", "installer", "scripts", "website", "integrations" };
        var metinler = new List<string>();

        foreach (var klasor in klasorler)
        {
            var tam = Path.Combine(kok, klasor.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(tam))
            {
                continue;
            }

            foreach (var dosya in Directory.EnumerateFiles(tam, "*.*", SearchOption.AllDirectories))
            {
                var parcalar = dosya.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parcalar.Any(parca =>
                        parca is "node_modules" or "bin" or "obj" or "dist"))
                {
                    continue;
                }

                if (!uzantilar.Contains(Path.GetExtension(dosya)))
                {
                    continue;
                }

                var normal = dosya.Replace('\\', '/');
                if (normal.EndsWith("i18n/tr.json", StringComparison.OrdinalIgnoreCase) ||
                    normal.EndsWith("i18n/en.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                metinler.Add(File.ReadAllText(dosya));
            }
        }

        Assert.True(metinler.Count > 200, $"Kaynak taraması bozuk olabilir: {metinler.Count} dosya okundu.");
        return metinler;
    }

    /// <summary>
    /// Çalışma anında kurulan anahtarların SABİT öneki. Üç biçim taranır: JS şablon dizesi
    /// (<c>`panel.x.${...}</c>), C# enterpolasyonu (<c>$"panel.x.{...}</c>) ve dizge birleştirme
    /// (<c>"panel.x." +</c>).
    /// </summary>
    private static IReadOnlyCollection<string> DinamikOnekler(IReadOnlyList<string> metinler)
    {
        // ELLE DOĞRULANAN kara liste: bunlar çeviri anahtarı DEĞİL, ana bilgisayar adı üretimidir
        // ($"mail.{domain}" gibi). Muaf sayılsalardı gerçek yetimleri gizlerlerdi -- nitekim
        // "mail." öneki, bayat "mail.unsubscribe_requested" anahtarını ilk ölçümde gizlemişti.
        var sahteOnekler = new HashSet<string>(StringComparer.Ordinal)
        {
            "mail.", "AdaPanel.", "_dmarc.", "autodiscover.", "ns1.", "sip.", "status.", "www.", "sitebuilder."
        };

        var desenler = new[]
        {
            new Regex(@"`([A-Za-z0-9_.]*?)\$\{", RegexOptions.Compiled),
            new Regex(@"\$@?""([A-Za-z0-9_.]*?)\{", RegexOptions.Compiled),
            new Regex(@"[""']([a-zA-Z0-9_.]+\.)[""']\s*\+", RegexOptions.Compiled)
        };

        var onekler = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metin in metinler)
        {
            foreach (var desen in desenler)
            {
                foreach (Match eslesme in desen.Matches(metin))
                {
                    var onek = eslesme.Groups[1].Value;
                    if (onek.Contains('.') && !sahteOnekler.Contains(onek))
                    {
                        onekler.Add(onek);
                    }
                }
            }
        }

        return onekler;
    }

    private static string BulKok()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        return dizin?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
