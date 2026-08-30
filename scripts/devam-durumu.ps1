# DEVAM DURUMU -- "yarida kesildim, agac saglam mi?" sorusunun TEK cevabi.
#
# NEDEN VAR -- olculmus bir kayip sinifi:
# Bu depoda is birden fazla ajana dagitiliyor ve kota/baglanti/elektrik yuzunden bir ajan
# CUMLENIN ORTASINDA durabiliyor. Bir kez oldu: durdurulan ajanlar SeoView.vue'yu tanimsiz bir
# fonksiyonla birakti; dosya derlenmiyordu ve bunu ancak bir sonraki oturumda, tesadufen fark
# ettik. Kayip is degildi -- kayip, agacin BOZUK OLDUGUNU BILMEMEKTI.
#
# COZUM BIR YEDEKLEME DEGIL, BIR OLCUMDUR:
#   * Her commit YESIL bir durumdur (bu depoda kural budur; testler gecmeden commit atilmaz).
#   * Yani commit'lenmemis her sey TANIMI GEREGI ucus halindedir.
#   * O halde kurtarma her zaman iki secenektir: ya bitir, ya `git checkout --` ile son yesile don.
# Bu betik hangisinin gerektigini SOYLER; karari kullaniciya birakir, kendiliginden HICBIR SEY
# SILMEZ. Otomatik geri alma bilerek YOK: yarim ama degerli bir isi sessizce yok etmek, yarim
# birakmaktan daha pahalidir.
#
# KULLANIM:
#   powershell -File scripts\devam-durumu.ps1           # tam olcum (~3 dk, dotnet dahil)
#   powershell -File scripts\devam-durumu.ps1 -Hizli    # yalniz derleme (~30 sn)

param([switch]$Hizli)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

function Yaz([string]$metin, [string]$renk = "Gray") { Write-Host $metin -ForegroundColor $renk }

Yaz "=== AdaPanel devam durumu ===" "Cyan"
Yaz ("Depo   : {0}" -f $repoRoot)
Yaz ("Zaman  : {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm"))

# ---------------------------------------------------------------------------------------------
# 1) SON YESIL NOKTA. Commit'lenmemis dosyalar ucus halindedir; once onlari GORUNUR yapariz.
# ---------------------------------------------------------------------------------------------
$sonCommit = (git log --oneline -1) 2>$null
Yaz ("Son kayit: {0}" -f $sonCommit)

$kirli = @(git status --porcelain)
if ($kirli.Count -eq 0) {
    Yaz "Calisma agaci TEMIZ -- commit'lenmemis degisiklik yok." "Green"
} else {
    Yaz ("Commit'lenmemis {0} dosya (UCUS HALINDE):" -f $kirli.Count) "Yellow"
    $kirli | ForEach-Object { Yaz ("   {0}" -f $_) "Yellow" }
}

# ---------------------------------------------------------------------------------------------
# 2) OLCUM. "Derleniyor mu" ile "dogru mu" ayri sorulardir; ikisini de sorariz.
#    JSON gecerliligi ayrica olculur: i18n dosyalarina ayni anda birden fazla ajan yaziyor ve
#    yarida kesilen bir yazma GECERSIZ JSON birakir -- bu, npm build'in vermedigi bir hatadir
#    (Vite bozuk JSON'da da bazen sessizce gecer, hata ancak tarayicida cikar).
# ---------------------------------------------------------------------------------------------
$hatalar = [System.Collections.Generic.List[string]]::new()

foreach ($dosya in @("frontend\src\i18n\tr.json", "frontend\src\i18n\en.json", "components.json", "release-body.json")) {
    $yol = Join-Path $repoRoot $dosya
    if (-not (Test-Path -LiteralPath $yol)) { continue }
    try {
        [void]([System.IO.File]::ReadAllText($yol, [System.Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop)
        Yaz ("JSON gecerli : {0}" -f $dosya) "Green"
    } catch {
        $hatalar.Add("GECERSIZ JSON: $dosya -- yarida kesilmis bir yazma olabilir.")
        Yaz ("JSON BOZUK   : {0}" -f $dosya) "Red"
    }
}

# i18n anahtar kumeleri esit mi: iki dosyaya paralel yazan ajanlar en cok BURAYI bozar.
$trYol = Join-Path $repoRoot "frontend\src\i18n\tr.json"
$enYol = Join-Path $repoRoot "frontend\src\i18n\en.json"
if ((Test-Path $trYol) -and (Test-Path $enYol)) {
    try {
        $tr = [System.IO.File]::ReadAllText($trYol, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $en = [System.IO.File]::ReadAllText($enYol, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $trAnahtar = $tr.PSObject.Properties.Name
        $enAnahtar = $en.PSObject.Properties.Name
        $yalnizTr = @($trAnahtar | Where-Object { $enAnahtar -notcontains $_ })
        $yalnizEn = @($enAnahtar | Where-Object { $trAnahtar -notcontains $_ })
        if ($yalnizTr.Count -eq 0 -and $yalnizEn.Count -eq 0) {
            Yaz ("i18n esit    : TR {0} = EN {1}" -f $trAnahtar.Count, $enAnahtar.Count) "Green"
        } else {
            $hatalar.Add("i18n anahtar kumeleri AYRISMIS: yalniz TR $($yalnizTr.Count), yalniz EN $($yalnizEn.Count).")
            Yaz ("i18n AYRISMIS: yalniz TR {0}, yalniz EN {1}" -f $yalnizTr.Count, $yalnizEn.Count) "Red"
            ($yalnizTr + $yalnizEn) | Select-Object -First 8 | ForEach-Object { Yaz ("   {0}" -f $_) "Red" }
        }
    } catch {
        $hatalar.Add("i18n karsilastirilamadi: $($_.Exception.Message)")
    }
}

# Mahremiyet kapisinin kendi sinamasi. NEDEN BURADA: kapi yalniz YAYIN aninda kosuyordu, yani
# bozuldugu ancak yayin denenince anlasilirdi -- ve 0.8.7'de kapinin "VM-" kalibinin dogustan olu
# oldugu tam da o anda ortaya cikti. Standart olcume baglanmasi, kapinin bozuldugunu yayindan
# ONCE gostermek icindir.
Yaz "-- mahremiyet kapisi" "Cyan"
& (Join-Path $PSScriptRoot "test-release-note-privacy.ps1")
if ($LASTEXITCODE -ne 0) {
    $hatalar.Add("Mahremiyet kapisi BOZUK -- duzeltmeden yayin yapilmamali.")
}

# ONEMLI -- BU BETIKTE `2>&1` YOK, ve bu bilincli bir karardir. OLCULDU: Windows PowerShell
# 5.1'de bir YEREL komutun (npm, node, dotnet) stderr'ini `2>&1` ile boru hattina almak, her
# satiri NativeCommandError'a sarar ve komut 0 ile bitse bile hata uretir. Betigin ilk surumu
# tam bu yuzden SAGLAM bir agaci "YARIM" diye raporladi -- yani kurtarma araci kendisi YANLIS
# ALARM veriyordu. Bir kurtarma aracinin yapabilecegi en kotu sey budur: gercek bir yarim isle
# ayirt edilemeyen sahte bir yarim. Karar YALNIZ $LASTEXITCODE'a bakilarak verilir; stderr
# zaten konsola dusuyor, gizlenmiyor.
Push-Location (Join-Path $repoRoot "frontend")
try {
    Yaz "-- npm run typecheck" "Cyan"
    $ciktisi = npm run typecheck
    if ($LASTEXITCODE -ne 0) {
        $ciktisi | Select-Object -Last 8 | ForEach-Object { Yaz ("   {0}" -f $_) "Red" }
        $hatalar.Add("typecheck BASARISIZ -- yarim kalmis bir .vue/.ts dosyasi var.")
    } else { Yaz "   temiz" "Green" }

    Yaz "-- npm run build" "Cyan"
    $ciktisi = npm run build
    if ($LASTEXITCODE -ne 0) {
        $ciktisi | Select-Object -Last 8 | ForEach-Object { Yaz ("   {0}" -f $_) "Red" }
        $hatalar.Add("build BASARISIZ.")
    } else { Yaz "   temiz" "Green" }

    Yaz "-- npm test" "Cyan"
    $ciktisi = npm test
    ($ciktisi | Select-String -Pattern "Tests\s+\d") | Select-Object -Last 1 | ForEach-Object { Yaz ("   {0}" -f $_) }
    if ($LASTEXITCODE -ne 0) {
        $hatalar.Add("npm test BASARISIZ.")
    }
} finally {
    Pop-Location
}

if (-not $Hizli) {
    Yaz "-- dotnet test (tam takim, ~2 dk)" "Cyan"
    $ciktisi = & (Join-Path $repoRoot ".tools\dotnet\dotnet.exe") test (Join-Path $repoRoot "tests\AdaPanel.Infrastructure.Tests\AdaPanel.Infrastructure.Tests.csproj") --nologo -v q
    ($ciktisi | Select-String -Pattern "Ba.ar.l.!|Ba.ar.s.z") | Select-Object -Last 1 | ForEach-Object { Yaz ("   {0}" -f $_) }
    if ($LASTEXITCODE -ne 0) { $hatalar.Add("dotnet test BASARISIZ.") }
} else {
    Yaz "dotnet test ATLANDI (-Hizli). Tam karar icin bayraksiz calistirin." "Yellow"
}

# ---------------------------------------------------------------------------------------------
# 3) KARAR. Tek cumlelik, tartismasiz.
# ---------------------------------------------------------------------------------------------
Yaz ""
Yaz "=== KARAR ===" "Cyan"

if ($hatalar.Count -eq 0) {
    Yaz "SAGLAM -- agac calisir durumda." "Green"
    if ($kirli.Count -gt 0) {
        Yaz "Commit'lenmemis is VAR ama saglam: bitince commit atilabilir." "Green"
    }
    Yaz "Devam: docs/calisma-durumu.md > DEVAM NOKTASI bolumundeki siradaki madde." "Green"
    exit 0
}

Yaz "YARIM -- agacta bitmemis/bozuk is var:" "Red"
$hatalar | ForEach-Object { Yaz ("   * {0}" -f $_) "Red" }
Yaz ""
Yaz "IKI SECENEK VAR (ucuncusu yok):" "Yellow"
Yaz "  (a) BITIR  -- yukaridaki dosyayi tamamla, sonra bu betigi tekrar calistir." "Yellow"
Yaz "  (b) GERI AL -- son YESIL commit'e don:" "Yellow"
Yaz "        git checkout -- <dosya>        # tek dosya" "Yellow"
Yaz "        git checkout -- .              # commit'lenmemis HER SEY (dikkat: geri donusu yok)" "Yellow"
Yaz ""
Yaz "Bu betik kendiliginden HICBIR SEY SILMEZ: yarim ama degerli bir isi sessizce" "Yellow"
Yaz "yok etmek, yarim birakmaktan daha pahalidir. Karar sizin." "Yellow"
exit 1
