# PAKETSIZ RELEASE NOBETCISI -- SAF KARAR, TEK KAYNAK.
#
# NEDEN AYRI DOSYA: karar publish-github-release.ps1'in govdesine gomuluyken TEST EDILEMIYORDU.
# Onu olcen uc test de yalnizca KAYNAK METNINI ariyordu ("su dize betikte geciyor mu"), yani
# koruma `if ($false) { ... }` icine alinsa ucu de YESIL kalirdi. Nobetcinin nobetcisi yoktu.
#
# Bu dosya yalnizca FONKSIYON TANIMLAR, hicbir sey CALISTIRMAZ ve ag/token istemez -- boylece
# test dot-source edip butun bayrak birlesimlerini gercekten kosturabilir.

<#
.SYNOPSIS
Yayin adiminda kurulum paketiyle ilgili ne yapilacagina karar verir.

.DESCRIPTION
Uc sonuctan birini doner:
  "yukle"        -- paket yuklenecek.
  "mevcut-yeter" -- yukleme istenmedi ama yayinda ZATEN paket var; yalniz not guncellenir.
  "engelle"      -- yukleme yapilmayacak VE yayinda paket yok; islem DURMALI.

OLCULMUS HATA (0.7.5): betik -InstallerPath VERILMEDEN calistirildi; release notlariyla olustu,
"Tamamlandi" yazdi, sifir hata koduyla dondu -- ama icinde indirilecek hicbir sey yoktu. Paketsiz
bir release panelin kendi kendine guncellemesini KIRAR: guncelleme ekrani yeni surumu GORUR,
indirmeye gider ve varlik bulamaz.

IKINCI HATA (ilk duzeltmede kacan): eski kosul yalnizca `-not $InstallerPath` durumuna bakiyordu.
`-InstallerPath X -SkipAssetUpload` BIRLIKTE verilince nobetci atlaniyordu ($InstallerPath dolu)
ve yukleme de atlaniyordu ($SkipAssetUpload) -- yani engellenmek istenen sonucun ta kendisi,
"Tamamlandi" diyerek uretiliyordu. Dogru soru "parametre verildi mi" DEGIL, "bu calismanin
sonunda yayinda paket olacak mi".

.PARAMETER InstallerPath
Yuklenecek kurulum dosyasinin yolu; verilmediyse bos.

.PARAMETER SkipAssetUpload
Yukleme bilerek atlaniyorsa $true. Yalniz notu duzeltmek MESRU bir istektir (yazim hatasi icin
her seferinde 86 MiB paket yuklemeye zorlamak, kurali etrafindan dolasilacak kadar pahali yapardi)
-- bu yuzden yasaklanmiyor, ACIKCA soylenmesi isteniyor.

.PARAMETER MevcutVarlikSayisi
Yayinda HALIHAZIRDA duran varlik sayisi. Release henuz yoksa 0.
#>
function Get-AdaReleaseAssetPlan {
    [OutputType([string])]
    param(
        [string] $InstallerPath,
        [bool] $SkipAssetUpload,
        [int] $MevcutVarlikSayisi
    )

    $yuklemePlanlandi = -not [string]::IsNullOrWhiteSpace($InstallerPath) -and -not $SkipAssetUpload
    if ($yuklemePlanlandi) {
        return "yukle"
    }

    if ($MevcutVarlikSayisi -gt 0) {
        return "mevcut-yeter"
    }

    return "engelle"
}

<#
.SYNOPSIS
"engelle" karari icin kullaniciya gosterilecek metni uretir -- karar ile metin ayri tutulur ki
karar test edilirken metin degisikligi testi kirmasin.
#>
function Get-AdaReleaseAssetBlockMessage {
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][string] $Tag)

    return ("Paketsiz yayin ENGELLENDI: '$Tag' icin bu calismanin sonunda yayinda kurulum dosyasi " +
            "OLMAYACAK.`n" +
            "  Paketi yuklemek icin : -InstallerPath artifacts\installer\AdaPanelSetup.exe`n" +
            "  YALNIZ notu guncelle : -SkipAssetUpload (yayinda zaten paket varsa)")
}
