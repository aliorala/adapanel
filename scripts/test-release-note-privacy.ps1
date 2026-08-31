# Yayin notu mahremiyet kapisinin KENDI sinamasi.
#
# NEDEN VAR: kapi (scripts/lib/release-note-privacy.ps1) 0.8.5'te gercek bir sizintidan sonra
# yazildi -- yayin notuna gelistiricinin FTP hesap adlari ve site adlari girmisti ve depo HERKESE
# ACIK. Kapi o sinifin tekrarini engellemek icin var.
#
# NEDEN AYRICA SINANIYOR: 0.8.7 yayini sirasinda kapinin "VM-" kalibinin DOGUSTAN OLU oldugu
# olculdu -- 'VM-[\d-]{5,}' yalniz RAKAM kabul ediyordu, yani "VM-ADAPANEL-01" gibi harf iceren
# gercek makine adlari suzgecten GECIYORDU. Kardes kaliplari (DESKTOP-, WIN-) dogruydu; uc kalip
# ayni satirda yan yana duruyordu ve fark edilmemisti.
#
# Ders: bir korumanin VARLIGI olctugunun kaniti degildir. Kapiyi iki yonde birden sinamak gerekir:
#   YAKALAMALI olani yakaliyor mu (koruma sahiden calisiyor mu),
#   GECMELI olani geciriyor mu   (yanlis alarm vermiyor mu).
# Ikincisi ilki kadar onemlidir: yanlis alarm veren bir dogrulama aracini insan susturmayi ogrenir,
# ve o alistiktan sonra GERCEK bulguyu da gecer. Aracin kendisinden daha tehlikeli hâle gelir.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib\release-note-privacy.ps1")

# Temel metin BILEREK gercek yayin notu degil: bu sinama surumden surume degismemeli.
# Icinde link-local adres ve ozel araliklar var, cunku 0.8.7 notu SSRF korumasini anlatirken
# bulut meta veri adresini (169.254.169.254) geciyor ve kapi onu YANLISLIKLA sizinti saymisti.
$temel = @"
# Ornek yayin notu

Panel artik ic aglara istek atmayi engelliyor. Bulut meta veri adresi 169.254.169.254 ve
ozel araliklar (10.0.0.5, 192.168.1.1, 172.16.0.1, 127.0.0.1) reddediliyor.

Ornek alan adi: musteri.com. Surum 0.8.7.1 olarak etiketlendi.
"@

$sinamalar = @(
    @{ Ad = "temiz not";     Ek = "";                                 Bekleniyor = "GECMELI" }
    @{ Ad = "genel IP";      Ek = "Sunucu 185.7.242.225 uzerinde.";   Bekleniyor = "YAKALAMALI" }
    @{ Ad = "e-posta";       Ek = "Iletisim: birisi@ornekposta.com";  Bekleniyor = "YAKALAMALI" }
    @{ Ad = "VM- makine";    Ek = "VM-ADAPANEL-01 uzerinde olculdu."; Bekleniyor = "YAKALAMALI" }
    @{ Ad = "DESKTOP-";      Ek = "DESKTOP-A1B2C3 makinesinde.";      Bekleniyor = "YAKALAMALI" }
    @{ Ad = "WIN-";          Ek = "WIN-8HJ2K9LM sunucusunda.";        Bekleniyor = "YAKALAMALI" }
    @{ Ad = "link-local";    Ek = "169.254.169.254 tekrar geciyor";   Bekleniyor = "GECMELI" }
    @{ Ad = "ornek adres";   Ek = "MAIL FROM:<fatura@baska.example>"; Bekleniyor = "GECMELI" }
    @{ Ad = "ornek.com";     Ek = "Iletisim: kisi@example.com";       Bekleniyor = "GECMELI" }
    @{ Ad = "sahte son ek";  Ek = "Adres: kisi@example.com.kotu.tld"; Bekleniyor = "YAKALAMALI" }
    @{ Ad = "panel surumu";  Ek = "Etiket 0.8.7.1 olarak atildi.";    Bekleniyor = "GECMELI" }
    @{ Ad = "belirsiz 4'lu"; Ek = "Bilesen surumu 1.2.3.4 idi.";      Bekleniyor = "YAKALAMALI" }
)

# "belirsiz 4'lu" NEDEN YAKALAMALI: "1.2.3.4" bir surum numarasi da olabilir gercek bir IP de, ve
# ikisini BAGLAM OLMADAN ayirt etmenin yolu yoktur. Bir mahremiyet kapisi icin dogru varsayilan
# ISARETLEMEKTIR: yanlis alarmin bedeli "insan bakip gecer", kacirmanin bedeli "gercek sunucu
# adresi herkese acik depoda kalir". Panelin KENDI surumu (0.8.7.1) sifirla basladigi icin zaten
# ozel-aralik dalina duser ve gecer; pratikte yanlis alarm cikaran sekil budur ve o kapali.
# Bu satir bir EKSIK degil, YAZILMIS bir karardir -- birisi "yanlis alarm" diye kaldirmasin.

$hata = 0
foreach ($s in $sinamalar) {
    $metin = if ($s.Ek) { $temel + [Environment]::NewLine + $s.Ek } else { $temel }

    # YASAKLI TERIM LISTESI BILEREK BOS: bu sinama kapinin DESEN tarafini olcer. Terim listesi
    # gitignore'daki privacy-terms.txt'ten gelir, makineden makineye degisir ve buraya karistirmak
    # sinamayi calistirandan calistirana farkli sonuc verir hâle getirirdi.
    $bulgular = @(Get-AdaReleaseNotePrivacyFindings -Metin $metin -YasakliTerimler @())

    $yakalandi = $bulgular.Count -gt 0
    $dogru = ($s.Bekleniyor -eq "YAKALAMALI") -eq $yakalandi
    if (-not $dogru) { $hata++ }

    $olan = if ($yakalandi) { "yakalandi" } else { "gecti" }
    $renk = if ($dogru) { "Green" } else { "Red" }
    $sonuc = if ($dogru) { "DOGRU" } else { "*** YANLIS ***" }
    Write-Host ("  {0,-12} beklenen={1,-11} olan={2,-10} {3}" -f $s.Ad, $s.Bekleniyor, $olan, $sonuc) -ForegroundColor $renk

    if (-not $dogru -and $yakalandi) {
        $bulgular | ForEach-Object { Write-Host ("                -> " + $_) -ForegroundColor Red }
    }
}

Write-Host ""
if ($hata -eq 0) {
    Write-Host ("  Mahremiyet kapisi DOGRU: {0}/{0}" -f $sinamalar.Count) -ForegroundColor Green
    exit 0
}

Write-Host ("  MAHREMIYET KAPISI BOZUK: {0} sinama yanlis." -f $hata) -ForegroundColor Red
Write-Host "  Kapi yanlis calisirken YAYIN YAPMAYIN -- kisisel veri herkese acik depoya gider." -ForegroundColor Red
exit 1
