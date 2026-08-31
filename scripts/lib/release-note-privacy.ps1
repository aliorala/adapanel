# YAYIN NOTU MAHREMIYET KAPISI -- saf karar fonksiyonu.
#
# NEDEN VAR -- yasanmis olay:
# Yayin notlari HERKESE ACIK bir depoda duruyor. Bir surumun notuna, hatayi kanitlamak icin
# yapilan CANLI OLCUMUN ciktisi oldugu gibi yapistirildi:
#
#     alioral_xyz_ftp   parola suresi: 13.09.2026
#     test_local_ftp    parola suresi: 10.10.2026
#
# Yani urunun yayin notu, musterinin gercek FTP hesap adlarini ve sunucusunun ic durumunu ifsa
# etti. Kullanici fark edip bildirdi; not elle temizlendi ve eski bir surumde (0.2.73) bir alan
# adi daha bulunup temizlendi.
#
# ASIL SORUN OLCUM DEGIL, OLCUMUN OLDUGU GIBI KOPYALANMASIDIR. Kanit toplamak dogrudur; kaniti
# yayin notuna tasirken kimligi silmek gerekir. Insan dikkati bunun icin yeterli bir savunma
# degil -- bu yuzden kapi otomatiklestirildi.
#
# TASARIM KARARI -- DAR TUTULDU, GENIS DEGIL:
# Bugun bu depoda ogrenilen ders, "yanlis alarm veren bir nobetci, olmayandan beterdir" oldu
# (bir dogrulama betigi saglam bir kurulumu "bulasik" diye raporladi ve guveni zedeledi). Bu
# yuzden burada YALNIZCA yuksek sinyalli uc sinif aranir:
#   1. GENEL IP adresi   -- yayin notunda genel bir IPv4, neredeyse her zaman gercek bir sunucudur.
#   2. Makine adi        -- VM-.. / DESKTOP-.. kaliplari kurulum makinesini ifsa eder.
#   3. E-posta adresi    -- dogrudan kisisel veri.
# Yerel/ozel adresler (127.*, 10.*, 192.168.*, 0.0.0.0) BILEREK serbest: belgede sik gecerler.
#
# Alan adlari desen ile ARANMAZ -- "musteri.com" gibi ornek adlar mesrudur ve desenle ayirt
# edilemez. Onun yerine cagiran taraf kendi yasakli terimlerini verir (bkz. -YasakliTerimler).

function Get-AdaReleaseNotePrivacyFindings {
    <#
    .SYNOPSIS
        Yayin notu metninde kisisel/altyapi izi arar. Bulgu listesi doner; bos liste = temiz.
    .PARAMETER Metin
        Yayinlanacak not govdesi (ve istenirse basligi).
    .PARAMETER YasakliTerimler
        Cagirana ozgu ek terimler (kendi alan adlari, hesap adlari). Buyuk/kucuk harf duyarsiz,
        duz metin olarak aranir -- desen degil, cunku alan adi deseni yanlis alarm uretir.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Metin,
        [string[]] $YasakliTerimler = @()
    )

    $bulgular = [System.Collections.Generic.List[string]]::new()

    if ([string]::IsNullOrWhiteSpace($Metin)) { return $bulgular.ToArray() }

    # --- 1) GENEL IPv4 -------------------------------------------------------------------------
    # Yerel/ozel araliklar disarida: belgede ve ornekte sik gecerler, yasaklamak yanlis alarm olur.
    foreach ($m in [regex]::Matches($Metin, '\b(?<a>\d{1,3})\.(?<b>\d{1,3})\.\d{1,3}\.\d{1,3}\b')) {
        $a = [int]$m.Groups['a'].Value
        $b = [int]$m.Groups['b'].Value

        # 0.x, 127.x (loopback), 10.x, 192.168.x, 172.16-31.x (ozel), 255.x, 224+ (coklu yayin)
        #
        # 169.254.x (LINK-LOCAL, RFC 3927) DA SERBEST -- ve bu satiri bir YANLIS ALARM yazdirdi:
        # 0.8.7 yayin notu SSRF korumasini anlatirken 169.254.169.254'u geciyor. O adres bulut
        # meta veri ucudur, dunyada HER sunucuda AYNIDIR ve bu yuzden kimseyi tanimlamaz; ustelik
        # link-local aralik kimseye ATANMAZ, arayuz kendi kendine yapilandirir. Yani burada
        # "genel IP" olmasi teknik olarak imkansiz. Kapiyi gevsetmiyoruz, YANLIS OLAN kurali
        # duzeltiyoruz: dogrulama araci yanlis alarm verirse insan onu susturmayi ogrenir ve
        # gercek bulguyu da gecer -- yanlis alarm, aracin kendisinden daha tehlikelidir.
        $ozel = ($a -eq 0) -or ($a -eq 127) -or ($a -eq 10) -or ($a -ge 224) -or
                ($a -eq 169 -and $b -eq 254) -or
                ($a -eq 192 -and $b -eq 168) -or ($a -eq 172 -and $b -ge 16 -and $b -le 31)

        # Surum numarasi degil: dort parcali bir surum (0.8.5.1) IP'ye benzer ama ilk parcasi
        # kucuktur ve nokta sayisi ayni. Ozel aralik denetimi bunlarin cogunu zaten yakalar.
        if (-not $ozel) {
            $bulgular.Add("genel IP adresi: $($m.Value)")
        }
    }

    # --- 2) MAKINE ADI -------------------------------------------------------------------------
    # VM- KALIBI ILK YAZILDIGINDA OLUYDU: 'VM-[\d-]{5,}' yalniz RAKAM kabul ediyordu, yani
    # "VM-ADAPANEL-01" gibi HARF iceren -- ve gercek hayatta en sik gorulen -- makine adlari
    # suzgecten geciyordu. Kardes kaliplar (DESKTOP-, WIN-) bastan [A-Z0-9] yaziyordu; uc kalip
    # tek satirda yan yana durdugu hâlde biri otekilerden farkliydi ve kimse fark etmemisti.
    # Yanlis alarm kovalarken yapilan mutasyon denemesi yakaladi: kapinin YAKALAMASI gerekeni
    # yakaladigini da sinamak, GECIRMESI gerekeni gecirdigini sinamak kadar gereklidir.
    foreach ($m in [regex]::Matches($Metin, '\b(?:VM-[A-Z0-9][A-Z0-9-]{4,}|DESKTOP-[A-Z0-9]{5,}|WIN-[A-Z0-9]{5,})\b', 'IgnoreCase')) {
        $bulgular.Add("makine adi: $($m.Value)")
    }

    # --- 3) E-POSTA ----------------------------------------------------------------------------
    # RFC 2606/6761 ILE AYRILMIS BELGELEME ALAN ADLARI SERBEST -- ve bu satiri bir YANLIS ALARM
    # yazdirdi: 0.8.12 notu, posta sahteciligini ORNEK BIR SMTP DOKUMUYLE anlatiyor ve icinde
    # "fatura@baska-musteri.example" geciyor. Bu adresler standartlarca TAM DA bu is icin
    # ayrilmistir; ".example", ".test", ".invalid", ".localhost" ve "example.com/net/org"
    # kimseye tahsis EDILEMEZ, dolayisiyla orada gercek bir kisinin adresi OLAMAZ.
    #
    # Kapi gevsetilmiyor, YANLIS OLAN kural duzeltiliyor -- link-local adreste alinan kararin
    # aynisi. Yanlis alarm veren bir dogrulama aracini insan susturmayi ogrenir ve alistiktan
    # sonra GERCEK bulguyu da gecer; aracin kendisinden tehlikeli hâle gelir.
    #
    # SON EKE bakilir, ICERMEYE degil: "example.com.kotu.tld" ayrilmis DEGILDIR ve yakalanmalidir.
    $belgelemeAlanlari = @('.example', '.test', '.invalid', '.localhost',
                           'example.com', 'example.net', 'example.org')

    foreach ($m in [regex]::Matches($Metin, '\b[A-Za-z0-9._%+-]+@(?<alan>[A-Za-z0-9.-]+\.[A-Za-z]{2,})\b')) {
        $alan = $m.Groups['alan'].Value.TrimEnd('.').ToLowerInvariant()
        $belgeleme = $belgelemeAlanlari | Where-Object { $alan -eq $_.TrimStart('.') -or $alan.EndsWith($_) }
        if (-not $belgeleme) {
            $bulgular.Add("e-posta: $($m.Value)")
        }
    }

    # --- 4) CAGIRANA OZGU YASAKLI TERIMLER -----------------------------------------------------
    foreach ($terim in $YasakliTerimler) {
        if ([string]::IsNullOrWhiteSpace($terim)) { continue }
        if ($Metin -match [regex]::Escape($terim)) {
            $bulgular.Add("yasakli terim: $terim")
        }
    }

    return $bulgular.ToArray()
}

function Get-AdaPrivacyTerms {
    <#
    .SYNOPSIS
        Depoya OZGU yasakli terimleri okur (scripts\lib\privacy-terms.txt). Dosya yoksa bos doner.
    .DESCRIPTION
        Terimler AYRI bir dosyada, cunku listenin kendisi kisisel veridir: kullanicinin alan
        adlarini betige gomulu tutmak, kacinmaya calistigimiz seyi kalicilastirirdi. Dosya
        .gitignore'dadir; yoksa kapi yalnizca desen tabanli uc sinifla calisir.
    #>
    param([string] $LibDizini = $PSScriptRoot)

    $yol = Join-Path $LibDizini "privacy-terms.txt"
    if (-not (Test-Path -LiteralPath $yol)) { return @() }

    return @(Get-Content -LiteralPath $yol -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") })
}
