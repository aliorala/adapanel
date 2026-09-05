using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Scheduler;

/// <summary>
/// Sunucuda kurulu Python yorumlayıcılarını bulur ve makine PATH'inden erişilebilir olup
/// olmadığını söyler.
/// <para>
/// NEDEN VAR: kullanıcının görevi "Python bulunamadı" ile düşüyordu, oysa sunucuda Python 3.11
/// kuruluydu. Sebep, Python kurulumunun VARSAYILAN seçeneği olan "yalnızca benim için" kurulum:
/// yorumlayıcı <c>%LOCALAPPDATA%\Programs\Python\...</c> altına iner ve YALNIZCA o kullanıcının
/// PATH'ine eklenir. Zamanlanmış görevler SYSTEM olarak çalıştığı için ne o klasörü PATH'inde
/// görür ne de o kullanıcının ortamını. Kullanıcı kendi terminalinde denediğinde çalıştığı için
/// bu fark neredeyse hiç fark edilmez.
/// </para>
/// <para>
/// TASARIM KARARI: bulunan yolu her görevin komut satırına gizlice enjekte ETMİYORUZ. Önce o yolu
/// denedim ve <c>cmd /c</c> tırnak sayımı yüzünden çalışmadığını ÖLÇTÜM; ama çalışsaydı bile
/// yanlış çözüm olurdu — her göreve görünmez bir ortam eklemek, sorunu bir sonraki sefere
/// saklamak olurdu. Bunun yerine gerçek eksiği söylüyoruz ve tek bir düğmeyle makine PATH'ine
/// ekliyoruz: kullanıcının elle yapacağı şeyin aynısı, ölçülebilir ve geri alınabilir.
/// </para>
/// </summary>
internal static class RuntimeLocator
{
    /// <summary>
    /// Bulunan yorumlayıcıları listeleyen PowerShell betiği. Her satır: "&lt;sürüm&gt;|&lt;tam yol&gt;".
    /// <para>
    /// Dört kaynağa birden bakılır çünkü Python kendini hepsine tutarlı biçimde yazmaz:
    /// makine kaydı, KULLANICI kayıtları (asıl vaka bu), bilinen klasörler ve py başlatıcısı.
    /// Yalnızca makine kaydına bakmak, tam da bu kullanıcının kurulumunu kaçırırdı.
    /// </para>
    /// </summary>
    internal const string DiscoveryScript = """
$adaBulunan = New-Object System.Collections.ArrayList

function Ada-Ekle([string]$yol) {
  if (-not $yol) { return }
  $exe = if ($yol.ToLowerInvariant().EndsWith('python.exe')) { $yol } else { Join-Path $yol 'python.exe' }
  if (Test-Path -LiteralPath $exe) {
    $surum = ''
    try { $surum = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { }
    if (-not $surum) { $surum = '0.0' }
    [void]$adaBulunan.Add("$surum|$exe")
  }
}

# 1) Makine geneli kurulumlar ("tum kullanicilar icin" secilmisse).
foreach ($kok in 'HKLM:\SOFTWARE\Python\PythonCore', 'HKLM:\SOFTWARE\WOW6432Node\Python\PythonCore') {
  if (Test-Path $kok) {
    foreach ($surum in Get-ChildItem $kok -ErrorAction SilentlyContinue) {
      $ip = Get-ItemProperty "$($surum.PSPath)\InstallPath" -ErrorAction SilentlyContinue
      if ($ip) { Ada-Ekle $ip.'(default)' }
    }
  }
}

# 2) KULLANICI kurulumlari. SYSTEM'in HKCU'su kendi profilidir, bu yuzden yuklu tum kullanici
#    kovanlari (HKU) taranir; kovan yuklu degilse 3. adimdaki klasor taramasi yakalar.
if (-not (Test-Path 'HKU:')) { New-PSDrive -Name HKU -PSProvider Registry -Root HKEY_USERS -ErrorAction SilentlyContinue | Out-Null }
foreach ($kullanici in Get-ChildItem 'HKU:\' -ErrorAction SilentlyContinue) {
  $kok = "HKU:\$($kullanici.PSChildName)\SOFTWARE\Python\PythonCore"
  if (Test-Path $kok) {
    foreach ($surum in Get-ChildItem $kok -ErrorAction SilentlyContinue) {
      $ip = Get-ItemProperty "$($surum.PSPath)\InstallPath" -ErrorAction SilentlyContinue
      if ($ip) { Ada-Ekle $ip.'(default)' }
    }
  }
}

# 3) Bilinen klasorler -- kayit defteri eksik/bozuk olsa da bulunur.
foreach ($desen in 'C:\Program Files\Python*\python.exe', 'C:\Program Files (x86)\Python*\python.exe', 'C:\Python*\python.exe', 'C:\Users\*\AppData\Local\Programs\Python\Python*\python.exe') {
  foreach ($dosya in (Get-ChildItem $desen -ErrorAction SilentlyContinue)) { Ada-Ekle $dosya.FullName }
}

# 4) SYSTEM'in kendi PATH'inde zaten var mi: "onarim gerekiyor mu" sorusunun cevabi budur.
$adaPathte = ''
$adaKomut = Get-Command python.exe -ErrorAction SilentlyContinue
if ($adaKomut) { $adaPathte = $adaKomut.Source }

foreach ($satir in ($adaBulunan | Sort-Object -Unique)) { [Console]::Out.WriteLine('ADAPY|' + $satir) }
[Console]::Out.WriteLine('ADAPATH|' + $adaPathte)
""";

    /// <summary>
    /// Keşif çıktısını ayrıştırır. En yeni sürüm başa alınır.
    /// <para>
    /// Sürüm karşılaştırması METİN olarak yapılmaz: "3.9" metinsel olarak "3.11"den büyüktür ve
    /// panel eski yorumlayıcıyı önerirdi.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<string> Interpreters, string OnPath) ParseDiscovery(string? output)
    {
        var yorumlayicilar = new List<(Version Surum, string Yol)>();
        var pathte = string.Empty;

        foreach (var satir in (output ?? string.Empty).Split('\n'))
        {
            var temiz = satir.Trim();
            if (temiz.StartsWith("ADAPATH|", StringComparison.Ordinal))
            {
                pathte = temiz["ADAPATH|".Length..].Trim();
            }
            else if (temiz.StartsWith("ADAPY|", StringComparison.Ordinal))
            {
                var parcalar = temiz["ADAPY|".Length..].Split('|', 2);
                if (parcalar.Length != 2 || string.IsNullOrWhiteSpace(parcalar[1]))
                {
                    continue;
                }

                yorumlayicilar.Add((ParseVersion(parcalar[0]), parcalar[1].Trim()));
            }
        }

        return (yorumlayicilar
            .GroupBy(item => item.Yol, StringComparer.OrdinalIgnoreCase)
            .Select(grup => grup.First())
            .OrderByDescending(item => item.Surum)
            .Select(item => item.Yol)
            .ToList(), pathte);
    }

    /// <summary>
    /// Sunucudaki php.exe'leri bulur; en yeni sürüm başta.
    /// <para>
    /// NEDEN PANELİN KENDİ KLASÖRÜ YETMEZ: panel PHP'yi yalnızca
    /// <c>%ProgramData%\AdaPanel\runtimes\php</c> altında arıyordu. Sunucuda PHP zaten kuruluysa
    /// (IIS ile gelen, elle kurulan ya da başka bir panelden kalan) görev "PHP kurulu değil"
    /// diyerek reddediliyordu — kullanıcı sitesinin PHP'sini gözüyle görürken. Python'daki hatanın
    /// aynısı: panel kendi bildiği tek yeri gerçeğin tamamı sanıyordu.
    /// </para>
    /// <para>
    /// IIS yapılandırması EN GÜVENİLİR kaynaktır: bir Windows web sunucusunda PHP hemen her zaman
    /// FastCGI olarak kayıtlıdır ve orada TAM YOLU yazar. Kayıtta <c>php-cgi.exe</c> geçer; komut
    /// satırı sürümü <c>php.exe</c> aynı klasördedir.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> FindPhpExecutables()
    {
        var adaylar = new List<string>();

        void Ekle(string? yol)
        {
            if (!string.IsNullOrWhiteSpace(yol) && File.Exists(yol))
            {
                adaylar.Add(Path.GetFullPath(yol));
            }
        }

        void KlasorTara(string kok, SearchOption derinlik)
        {
            try
            {
                if (Directory.Exists(kok))
                {
                    foreach (var dosya in Directory.EnumerateFiles(kok, "php.exe", derinlik))
                    {
                        Ekle(dosya);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Erisilemeyen bir klasor digerlerini aramayi engellememeli.
            }
        }

        // 1) Panelin kendi kurdugu surumler -- tercih sirasinda ONCE gelir.
        KlasorTara(Environment.ExpandEnvironmentVariables(@"%ProgramData%\AdaPanel\runtimes\php"), SearchOption.AllDirectories);

        // 2) IIS FastCGI kayitlari.
        foreach (var klasor in ReadPhpFoldersFromIisConfig(
                     Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\inetsrv\config\applicationHost.config")))
        {
            Ekle(Path.Combine(klasor, "php.exe"));
        }

        // 3) Bilinen kurulum yerleri.
        foreach (var kok in new[]
                 {
                     @"C:\Program Files\PHP",
                     @"C:\Program Files (x86)\PHP",
                     @"C:\PHP",
                     @"C:\php",
                     @"C:\tools"
                 })
        {
            KlasorTara(kok, SearchOption.AllDirectories);
        }

        return adaylar
            .GroupBy(yol => yol, StringComparer.OrdinalIgnoreCase)
            .Select(grup => grup.First())
            .OrderByDescending(ParseVersionFromPath)
            .ToList();
    }

    /// <summary>
    /// applicationHost.config içindeki FastCGI kayıtlarından PHP klasörlerini çıkarır.
    /// <para>
    /// XML olarak değil METİN olarak okunur: dosya bozuk ya da kısmen okunabilir olsa bile
    /// elimizdekiyle devam edebilmek, hiçbir şey bulamamaktan iyidir — ve burada tek yaptığımız
    /// şey aday yol toplamak, sonra hepsi <see cref="File.Exists(string)"/> ile doğrulanıyor.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ReadPhpFoldersFromIisConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return [];
            }

            return Regex.Matches(File.ReadAllText(configPath), "fullPath=\"([^\"]*php-cgi\\.exe)\"", RegexOptions.IgnoreCase)
                .Select(match => Path.GetDirectoryName(match.Groups[1].Value))
                .Where(klasor => !string.IsNullOrWhiteSpace(klasor))
                .Select(klasor => klasor!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Yoldaki sürüm numarasını çıkarır (ör. ...\php\8.3.6\php.exe → 8.3.6).
    /// Metinsel sıralama "8.9" &gt; "8.10" derdi; sürüm seçimi sayısal olmalı.
    /// </summary>
    internal static Version ParseVersionFromPath(string path) => ParseVersion(path);

    private static Version ParseVersion(string value)
    {
        var eslesme = Regex.Match(value ?? string.Empty, @"(\d+)\.(\d+)(?:\.(\d+))?");
        return eslesme.Success && Version.TryParse(
            $"{eslesme.Groups[1].Value}.{eslesme.Groups[2].Value}.{(eslesme.Groups[3].Success ? eslesme.Groups[3].Value : "0")}",
            out var surum)
            ? surum
            : new Version(0, 0, 0);
    }

    /// <summary>
    /// Klasörü makine PATH'ine ekleyen betik. Zaten varsa hiçbir şey yapmaz.
    /// <para>
    /// DİKKAT — <c>[Environment]::SetEnvironmentVariable(..., 'Machine')</c> KULLANILMAZ: o çağrı
    /// PATH'i REG_EXPAND_SZ'den REG_SZ'ye çevirir ve içindeki <c>%SystemRoot%</c> gibi
    /// değişkenler bir daha genişletilmez — yani sunucunun PATH'i sessizce bozulur. Bu yüzden
    /// değer tipi AÇIKÇA ExpandString olarak yazılır.
    /// </para>
    /// <para>
    /// Scripts klasörü de eklenir: pip ile kurulan araçlar (yt-dlp, scrapy…) oraya iner ve
    /// betikler onları çoğu zaman doğrudan çağırır.
    /// </para>
    /// </summary>
    internal static string BuildAddToMachinePathScript(string interpreterPath)
    {
        var klasor = Path.GetDirectoryName(interpreterPath) ?? string.Empty;
        var scripts = Path.Combine(klasor, "Scripts");

        return $$"""
$ErrorActionPreference = 'Stop'
$adaAnahtar = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$adaMevcut = (Get-ItemProperty -Path $adaAnahtar -Name Path -ErrorAction Stop).Path
$adaEklenecek = @('{{klasor.Replace("'", "''")}}', '{{scripts.Replace("'", "''")}}')
$adaParcalar = $adaMevcut.Split(';') | Where-Object { $_ }
$adaYeni = @()
foreach ($aday in $adaEklenecek) {
  if (-not (Test-Path -LiteralPath $aday)) { continue }
  $var = $false
  foreach ($parca in $adaParcalar) { if ($parca.TrimEnd('\') -ieq $aday.TrimEnd('\')) { $var = $true } }
  if (-not $var) { $adaYeni += $aday }
}

if ($adaYeni.Count -eq 0) {
  [Console]::Out.WriteLine('ADAPANEL-DEGISIKLIK=0')
} else {
  $adaSonuc = ($adaParcalar + $adaYeni) -join ';'
  # -ErrorAction Stop YOKTU. Bu depoda ÖLÇÜLMÜŞ bir sınıf: sonlandırıcı OLMAYAN bir hatada
  # betik devam eder ve powershell.exe SIFIR ile çıkar — yani panel "eklendi" der, HKLM'de
  # hiçbir şey değişmemiştir. Burası MAKİNE GENELİ bir yazmadır.
  Set-ItemProperty -Path $adaAnahtar -Name Path -Value $adaSonuc -Type ExpandString -ErrorAction Stop
  [Console]::Out.WriteLine('ADAPANEL-DEGISIKLIK=' + $adaYeni.Count)
}
{{MakinePathDogrulamaParcasi}}
""";
    }

    /// <summary>
    /// Makine PATH'ini TAZE okuyup <c>$adaEklenecek</c>'in kaçının gerçekten içeride olduğunu
    /// basan betik parçası.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TEK KAYNAK.</b> Aynı kod hem ekleme betiğinin sonunda hem de yalnız-okuyan sondada
    /// koşar; yani karar kuralı GERÇEK <c>powershell.exe</c>'de, hiçbir şey yazmadan ölçülebilir.
    /// </para>
    /// <para>
    /// <c>ADAPANEL-PATH-HEDEF</c> = DİSKTE VAR OLAN aday sayısı (var olmayan klasörü PATH'e
    /// koymayız, hedefe de saymayız). <c>ADAPANEL-PATH-DOGRULANDI</c> = TAZE okunan PATH'te
    /// bulunanların sayısı. Panelin bugüne kadar raporladığı sayı ise EKLENMESİ İSTENEN
    /// sayıydı — hiçbir ölçüme dayanmıyordu.
    /// </para>
    /// </remarks>
    internal const string MakinePathDogrulamaParcasi = """
$adaTazeAnahtar = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$adaTaze = (Get-ItemProperty -Path $adaTazeAnahtar -Name Path -ErrorAction Stop).Path
$adaTazeParcalar = $adaTaze.Split(';') | Where-Object { $_ }
$adaHedef = 0
$adaDogru = 0
foreach ($aday in $adaEklenecek) {
  if (-not (Test-Path -LiteralPath $aday)) { continue }
  $adaHedef++
  foreach ($parca in $adaTazeParcalar) {
    if ($parca.TrimEnd('\') -ieq $aday.TrimEnd('\')) { $adaDogru++; break }
  }
}
[Console]::Out.WriteLine('ADAPANEL-PATH-HEDEF=' + $adaHedef)
[Console]::Out.WriteLine('ADAPANEL-PATH-DOGRULANDI=' + $adaDogru)
""";

    /// <summary>
    /// Yalnız OKUYAN sonda: verilen klasörlerin makine PATH'inde olup olmadığını ölçer.
    /// HİÇBİR ŞEY YAZMAZ — yükseltilmemiş bir süreçte de koşar.
    /// </summary>
    internal static string BuildMachinePathProbeScript(IEnumerable<string> klasorler)
    {
        var liste = string.Join(", ", klasorler.Select(item => "'" + item.Replace("'", "''") + "'"));
        return "$ErrorActionPreference = 'Stop'\n$adaEklenecek = @(" + liste + ")\n" + MakinePathDogrulamaParcasi;
    }
}
