using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService(
    IIisSiteService iisSiteService,
    IPanelPreferencesService panelPreferencesService,
    JsonFileStore fileStore,
    ILogger<DnsService> logger) : IDnsService
{
    private readonly DnsAvailabilityProbe dnsAvailability = new();
    private const string StatePath = "config/dns-zones.json";
    private const string TemplatePath = "config/dns-template.json";
    private const string WatchdogPath = "config/dns-watchdog.json";
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WatchdogCooldown = TimeSpan.FromHours(1);
    // 2: posta kayitlari (mail A, webmail A, MX, SPF, DMARC) varsayilan sete eklendi. Surum artinca
    // MevcutDefaults birlestirmesi calisir -> ESKI bolgeler de eksik posta kayitlarini kendiliginden alir
    // (kullanicinin ekledigi kayitlara dokunulmaz, yalniz EKSIK olanlar eklenir).
    // 3: istemci otomatik yapilandirma (RFC 6186 SRV) + TLS raporlama eklendi.
    // 4: 3 numarali birlestirme HICBIR ESKI BOLGEDE ISE YARAMADI ve bunu olcerek gorduk.
    //    Sebep: o sirada BuildDefaultRecordsAsync kayitli DNS sablonunu bulunca yerlesik
    //    varsayilanlari tamamen golgeliyordu (0.2.62'de duzeltildi). Yani birlestirme calisti,
    //    eklenecek kayit olarak SADECE sablondakileri gordu, SRV'leri hic uretmedi -- ama surum
    //    yine de 3 olarak damgalandi. Damga atildigi icin duzeltilmis birlestirme BIR DAHA
    //    calismayacakti: canli sunucuda iki alan adinda da 4 SRV + _smtp._tls eksik kaldi
    //    (harici DNS sorgusuyla dogrulandi). Surumu artirmak, duzeltilmis birlestirmeyi eski
    //    bolgelerde bir kez daha calistirir. Birlestirme yalnizca EKSIK olani ekler, bu yuzden
    //    kullanicinin sildigi ya da degistirdigi kayitlar icin risk yok.
    internal const int CurrentDefaultsVersion = 4;
    // Desteklenen kayıt tipleri. SRV/PTR/TLSA'nın Windows DNS'te NATIVE cmdlet parametresi vardır;
    // CAA'nın yoktur, RFC 3597 "bilinmeyen kayıt" yolu (-Unknown -Type 257 + ham RDATA) ile yazılır.
    // Değer biçimleri (UI'da da gösterilir):
    //   SRV   -> değer: "<ağırlık> <port> <hedef>", öncelik ayrı alanda
    //   CAA   -> değer: "<bayrak> <etiket> <değer>"   ör: 0 issue letsencrypt.org
    //   TLSA  -> değer: "<kullanım> <seçici> <eşleşme> <sertifika-hex>"
    //   PTR   -> değer: hedef alan adı
    private static readonly string[] SupportedTypes =
        ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA", "TLSA", "PTR"];

    /// <summary>
    /// Kayıt panele yazıldı ama Windows DNS'e YAYINLANMADI: DNS Server rolü kurulu değil.
    /// </summary>
    /// <remarks>
    /// Adı <c>...Key</c> ile bitiyor çünkü <c>MessageKeyCoverageTests</c> nöbetçisi tam olarak bu
    /// biçimdeki atamaları tarıyor: anahtar burada bir SABİT olarak durduğu sürece, çevirisi
    /// unutulursa test kızarır. Sabiti kaldırıp yerine dizgeyi <c>return</c> etseydik nöbetçi
    /// göremezdi ve kullanıcı ekranda ham anahtarı görürdü.
    /// </remarks>
    private const string NotPublishedNoServerKey = "dns.not_published_no_server";

    /// <summary>
    /// Bölge ikincil işaretli ama master listesi boş: yayınlanacak bir kaynak yok.
    /// </summary>
    private const string NotPublishedSecondaryNoMasterKey = "dns.not_published_secondary_no_master";

    public async Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            var state = await GetOrCreateStateAsync(site, cancellationToken);
            var yayinAnahtari = await TrySyncWindowsDnsAsync(state, cancellationToken);
            if (yayinAnahtari.Length > 0)
            {
                // Yayınlanmadı ama İSTİSNA da atılmadı: aşağıdaki catch bunu görmezdi. Sessiz
                // kalırsak "site kuruldu, bölge yayınlanmadı" durumu hiçbir yerde iz bırakmaz.
                logger.LogInformation(
                    "Bolge Windows DNS'e yayinlanmadi ({SiteName}): {Anahtar}",
                    siteName,
                    yayinAnahtari);
            }
        }
        catch (Exception exception)
        {
            // En iyi caba: Windows DNS yoksa/yazilamazsa SITE OLUSTURMAYI bozmayiz. Ama sessiz de
            // kalmayiz -- eskiden bos catch vardi ve kurulusta yayinlanamayan bir bolge HICBIR
            // yerde gorunmuyordu; kullanici alan adinin neden cozulmedigini aylarca arayabilirdi.
            // Onarma bu durumu ayrica tespit edip yeniden yayinlar.
            logger.LogWarning(
                exception,
                "Bolge Windows DNS'e yayinlanamadi ({SiteName}). Sunucu Sagligi > Eksikleri tamamla ile yeniden denenebilir.",
                siteName);
        }
    }

    public async Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            // Install-WindowsFeature yalnız Windows Server'da vardir (istemci OS'ta yok).
            var install = await ProcessRunner.RunPowerShellAsync(
                "if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) { exit 21 }; " +
                "Install-WindowsFeature -Name DNS -IncludeManagementTools | Out-Null",
                cancellationToken);

            if (install.ExitCode == 21)
            {
                throw new AppException(
                    "dns.windows_install_unsupported",
                    400,
                    400,
                    "Install-WindowsFeature is not available. The Windows DNS Server role requires Windows Server.");
            }

            if (!install.Success)
            {
                throw new AppException("dns.windows_install_failed", 500, 500, BuildCommandDetails(install));
            }
            dnsAvailability.Invalidate();
        }

        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.ZoneName = NormalizeZoneName(string.IsNullOrWhiteSpace(request.ZoneName) ? state.ZoneName : request.ZoneName);
        state.NameServers = NormalizeNameServers(request.NameServers, state.ZoneName);
        state.ZoneTransferIps = NormalizeZoneTransferIps(request.ZoneTransferIps);

        // İKİNCİL BÖLGEDE YÖNETİLEN KAYITLAR YENİDEN KURULMAZ.
        //
        // Bölge ayarlarını kaydetmek (ad, ad sunucuları) ikincil kipte de anlamlıdır — bunlar
        // panelin kendi kaydıdır ve birincile dönüldüğünde kullanılır. Ama varsayılan kayıt setini
        // yeniden üretmek, master'dan gelen içeriğin ÜSTÜNE panelin tahminini koymaya çalışmaktır;
        // senkron bunu zaten yazmaz, geriye yalnızca panelde yanlış görünen bir liste kalırdı.
        if (!state.IsSecondary)
        {
            state.Records.RemoveAll(record => record.IsManaged);
            state.Records.AddRange(await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
            state.DefaultsVersion = CurrentDefaultsVersion;
            state.Records = SortRecords(state.Records).ToList();
        }

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        EnsureZoneIsWritable(state);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.Records.RemoveAll(record => record.IsManaged);
        state.Records.AddRange(await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> SetZoneModeAsync(
        string siteName,
        SetDnsZoneModeRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        if (request.Secondary)
        {
            // MASTER ZORUNLU. Master'sız bir ikincil bölge hiçbir zaman veri çekemez; bölgeyi
            // ikincile çevirip listeyi boş bırakmak, alan adını dünyaya karşı çözülemez yapmanın
            // en hızlı yoludur. Bu yüzden bölge kipi DEĞİŞMEDEN önce reddedilir.
            var masters = NormalizeIpList(request.MasterServers, "dns.invalid_master_ip");
            if (masters.Count == 0)
            {
                throw new AppException("dns.master_required", 400, 400);
            }

            state.MasterServers = masters;
            state.IsSecondary = true;
        }
        else
        {
            state.IsSecondary = false;
            // Master listesi SİLİNMEZ: kullanıcı kipler arasında gidip gelirken IP'leri yeniden
            // yazmak zorunda kalmasın. Birincil kipte hiçbir yerde kullanılmaz.
        }

        await UpsertStateAsync(state, cancellationToken);

        // Windows DNS yoksa kip yine de KAYDEDİLİR (panel durumu doğrudur), ama sessiz kalmayız:
        // senkron hatası çağırana AppException olarak çıkar.
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> TransferZoneNowAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        // Birincil bölgede AXFR başlatmak anlamsızdır (çekilecek bir master yok) ve Windows da
        // reddeder. "Hiçbir şey olmadı" yerine sebebi söylüyoruz.
        if (!state.IsSecondary)
        {
            throw new AppException("dns.transfer_requires_secondary", 409, 409);
        }

        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            throw new AppException("dns.transfer_requires_windows", 409, 409);
        }

        var script =
            "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
            $"Start-DnsServerZoneTransfer -Name '{EscapePowerShellLiteral(state.ZoneName)}' -FullTransfer | Out-Null";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("dns.transfer_failed", 500, 500, BuildCommandDetails(result));
        }

        return await ToDtoAsync(state, cancellationToken);
    }

    /// <summary>
    /// Bölge ikincilse KAYIT YAZMA girişimini reddeder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neden sessizce yazıp geçmiyoruz: ikincil bölgenin içeriği master'dan gelir. Kaydı panel
    /// dosyasına yazıp "kaydedildi" demek, kullanıcıya var olmayan bir kayıt göstermek olurdu —
    /// hem panelde görünür hem de dünyada YOK. Panelin en pahalı hata sınıfı tam olarak budur:
    /// yapılmamış işi yapılmış saymak.
    /// </para>
    /// </remarks>
    private static void EnsureZoneIsWritable(DnsZoneState state)
    {
        if (state.IsSecondary)
        {
            throw new AppException("dns.zone_is_secondary", 409, 409);
        }
    }

    public async Task<IReadOnlyList<string>> RepairDefaultsAsync(
        string siteName,
        bool applyChanges = true,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken, persist: applyChanges);

        // İKİNCİL BÖLGEDE ONARILACAK BİR ŞEY YOK — ve hata da FIRLATILMAZ.
        //
        // Bu metot "alan adlarını tazele" toplu akışından her site için çağrılıyor. Tek bir ikincil
        // bölge yüzünden fırlatmak, o çağrıdaki DİĞER sitelerin onarımını da durdururdu. Boş liste
        // doğru cevap: bu bölgede panelin ekleyeceği kayıt yok.
        if (state.IsSecondary)
        {
            return [];
        }

        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        var defaults = await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken);

        // Eklenenleri sayabilmek için önce/sonra kimlik kümesi karşılaştırılır. "Kaç kayıt eklendi"
        // bilgisi bu ekranın tek değeri: kullanıcı neyin değiştiğini görmeden bir onarıma güvenmez.
        var before = state.Records.Select(RecordKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Kuru çalıştırmada state nesnesinin KOPYASI üzerinde çalışılır: GetOrCreateStateAsync geriye
        // paylaşılan bir nesne verdiği için doğrudan üzerine eklemek, hiçbir şey yazmasak bile aynı
        // istek içindeki sonraki okumalara "sanki eklenmiş" bir zone gösterirdi.
        var workingRecords = applyChanges ? state.Records : [.. state.Records];
        MergeMissingDefaults(workingRecords, defaults);

        var added = workingRecords
            .Where(record => !before.Contains(RecordKey(record)))
            .Select(record => $"{record.Type} {record.Host}")
            .ToList();

        if (!applyChanges || added.Count == 0)
        {
            // Yazma yok: dosyaya dokunmadan çık. Kuru çalıştırma zaten yazmamalı; eksik yoksa da
            // yüzlerce zone için gereksiz yazma + Windows DNS eşitlemesi işlemi dakikalarca uzatırdı.
            return added;
        }

        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return added;
    }

    /// <summary>
    /// Bu kayit, konak basina yalnizca BIR tane bulunabilecek bir TXT politikasi mi?
    /// Oyleyse politikanin onekini doner (eslesme degere degil ONEKE gore yapilir).
    /// <para>
    /// DKIM burada YOKTUR ve olmamalidir: her secici (selector) icin ayri bir kayit gerekir,
    /// zaten konaklari da farklidir (&lt;secici&gt;._domainkey).
    /// </para>
    /// </summary>
    private static string? ResolveSingletonTxtPrefix(string type, string value)
    {
        if (!string.Equals(type, "TXT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
        {
            return "v=spf1";
        }

        return trimmed.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) ? "v=DMARC1" : null;
    }

    public async Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            return null;
        }

        // Tek PowerShell turunda tum bolge okunur: kayit basina surec baslatmak, 40 siteli bir
        // sunucuda denetimi dakikalara cikarirdi.
        var script =
            $"Get-DnsServerResourceRecord -ZoneName '{EscapePowerShellLiteral(state.ZoneName)}' -ErrorAction Stop | " +
            "ForEach-Object { $_.RecordType + '|' + $_.HostName }";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            // Olcemedik: "eksik yok" DEMEK YERINE bilmedigimizi soyluyoruz. Yanlis bir "temiz"
            // raporu, sorunu gizlemenin en kolay yolu.
            return null;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains('|'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Satir ayiricilari; PowerShell ciktisi CRLF de LF de olabilir.</summary>
    private static readonly char[] NewLineSeparators = [(char)13, (char)10];

    private static string RecordKey(DnsRecordState record) => $"{record.Type}|{record.Host}|{record.Value}";

    public async Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        // IKINCIL BOLGE: OTOMASYON DURDURULMAZ, yalnizca bolgeye YAZILMAZ.
        //
        // Bu asiri yukleme kullanicinin dogrudan bir eylemi degil; posta kutusu acma, otomatik
        // yapilandirma ve alan adi tazeleme akislari cagiriyor. AppException firlatmak, DNS'i
        // baska bir yerde barindiran bir musterinin posta kutusu ACAMAMASINA yol acardi -- oysa
        // ikisinin birbirine bagli olmasi icin bir sebep yok (Plesk'te de yok).
        //
        // Kayitlar panel durumuna YAZILIR: bolge birincile dondugunde hepsi yeniden yayinlanir.
        // Yayinlanmadiklari da gizlenmez; bolge DTO'su ikincil oldugunu soyler ve "yayinlanmis
        // kayitlar" denetimi (GetPublishedRecordKeysAsync) bunlari eksik olarak gosterir.
        if (state.IsSecondary)
        {
            foreach (var request in requests)
            {
                ApplyRecordToState(state, NormalizeRecord(request, state.ZoneName), request.Id);
            }

            state.Records = SortRecords(state.Records).ToList();
            await UpsertStateAsync(state, cancellationToken);
            logger.LogInformation(
                "Bolge ikincil ({Zone}); {Sayi} kayit panelde saklandi ama Windows DNS'e YAZILMADI (kaynak master).",
                state.ZoneName,
                requests.Count);
            return await ToDtoAsync(state, cancellationToken);
        }

        // Tum kayitlar duruma yazilir, senkron SONDA BIR KEZ calisir.
        //
        // Tek tek yazmak her seferinde bolgenin tamamini yeniden yazan bir senkron baslatiyordu:
        // hem cok yavas hem de pes pese gelen senkronlar birbirinin bolge kilidine carpiyor,
        // kayitlar "zaten var, silinemedi, eklenemedi" durumunda kaliyordu.
        foreach (var request in requests)
        {
            ApplyRecordToState(state, NormalizeRecord(request, state.ZoneName), request.Id);
        }

        state.Records = SortRecords(state.Records).ToList();
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    /// <summary>
    /// Bir kaydi durum listesine uygular (guncelle ya da ekle). Yazma/senkron YAPMAZ -- cagiran
    /// toplu isleyip tek seferde senkronlayabilsin diye ayrildi.
    /// </summary>
    private static void ApplyRecordToState(DnsZoneState state, DnsRecordState record, string? requestId)
    {
        record.IsManaged = false;

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            var index = state.Records.FindIndex(item => string.Equals(item.Id, requestId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new AppException("dns.record_not_found", 404, 404);
            }

            record.Id = state.Records[index].Id;
            state.Records[index] = record;
        }
        else
        {
            // TEK ORNEKLI politika kayitlari: bir konakta yalnizca BIR SPF ve BIR DMARC olabilir.
            // Standart bunu acikca soyler; ikinci bir kayit dogrulamayi "permerror" yapar, yani
            // politika hic yokmus gibi -- hatta daha kotusu, gonderen sunucular postayi reddeder.
            //
            // Neden gerekliydi: eslesme TUR+KONAK+DEGER uzerindendi. SPF degeri panelde iki ayri
            // yerde uretiliyor (DNS sablonu sitenin baglama IP'sini tercih ediyor, posta tarafi
            // yalnizca panel tercihindeki adrese bakiyor ve o adres IPv4 degilse "ip4:" bolumunu
            // hic yazmiyor). Degerler farkli oldugu icin upsert IKINCI bir SPF ekliyordu; her
            // onarmada fark yeniden goruluyor ve kullanici "onardim ama uyari geri geliyor"
            // diyordu. Artik ayni politikanin eskisi DEGISTIRILIYOR, yenisi eklenmiyor.
            var policyPrefix = ResolveSingletonTxtPrefix(record.Type, record.Value);
            // YALNIZ PANELIN YONETTIGI kayit degistirilir.
            //
            // Kullanici SPF'ine "include:_spf.google.com" eklemis ya da DMARC'i "p=reject" yapmis
            // olabilir. Tek-ornek kurali bunlari da ezerse, onarma her calistiginda kullanicinin
            // eli emegini panel varsayilanina geri dondururdu -- ki panelin sozu tam tersi:
            // "eksikler eklenir, var olana dokunulmaz".
            // An identical user-owned policy is already the desired record. The managed-only
            // policy fallback below must not turn every repeated upsert into another copy.
            var duplicate = state.Records.FirstOrDefault(item =>
                string.Equals(item.Type, record.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Value, record.Value, StringComparison.Ordinal) &&
                item.Priority == record.Priority);
            duplicate ??= policyPrefix is not null
                ? state.Records.FirstOrDefault(item =>
                    item.IsManaged &&
                    string.Equals(item.Type, "TXT", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
                    item.Value.StartsWith(policyPrefix, StringComparison.OrdinalIgnoreCase))
                : state.Records.FirstOrDefault(item =>
                    string.Equals(item.Type, record.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, record.Value, StringComparison.OrdinalIgnoreCase));

            if (duplicate is not null)
            {
                record.Id = duplicate.Id;
                state.Records[state.Records.IndexOf(duplicate)] = record;
            }
            else
            {
                state.Records.Add(record);
            }
        }

    }

    public async Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default)
    {
        var (state, _) = await UpsertRecordCoreAsync(siteName, request, cancellationToken);

        // Yayın anahtarı BURADA atılabilir: dönen DTO'nun `StatusMessage` alanı zaten ölçülmüş
        // durumu taşıyor (`dns.windows_dns_unavailable` / `dns.windows_zone_missing`) ve DNS
        // ekranı onu basıyor. Anahtara ihtiyacı olan, DTO'yu HİÇ görmeyen çağıranlardır.
        return await ToDtoAsync(state, cancellationToken);
    }

    /// <summary>
    /// Kaydı panel durumuna yazar ve yayınlar; yayın SONUCUNU da döndürür.
    /// </summary>
    /// <remarks>
    /// <see cref="UpsertRecordAsync"/> yalnız DTO döndürdüğü için "yayınlandı mı" bilgisi
    /// kayboluyordu. <see cref="EnsureSubdomainRecordAsync"/> tam olarak bu bilgiye muhtaç:
    /// site oluşturma ekranına "oluşturuldu" mu yoksa "panelde duruyor ama yayınlanmadı" mı
    /// yazacağına ona göre karar veriliyor.
    /// </remarks>
    private async Task<(DnsZoneState State, string PublishKey)> UpsertRecordCoreAsync(
        string siteName,
        UpsertDnsRecordRequest request,
        CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        EnsureZoneIsWritable(state);
        ApplyRecordToState(state, NormalizeRecord(request, state.ZoneName), request.Id);

        state.Records = SortRecords(state.Records).ToList();
        await UpsertStateAsync(state, cancellationToken);
        return (state, await TrySyncWindowsDnsAsync(state, cancellationToken));
    }

    public async Task<SubdomainRecordResult> EnsureSubdomainRecordAsync(
        string parentSiteName,
        string label,
        CancellationToken cancellationToken = default)
    {
        var etiket = (label ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (etiket.Length == 0)
        {
            return new SubdomainRecordResult { Error = "label_empty" };
        }

        // BÖLGE VAR MI — yoksa OLUŞTURULMAZ.
        //
        // Burada bilerek GetOrCreateStateAsync çağrılmıyor: o metot bölgesi olmayan siteye bölge
        // KURAR. Alt alan adı sitesi açmak, üst alan adı için panelde DNS yönetimini başlatmak
        // demek değildir; alan adının DNS'i başka bir sağlayıcıda olabilir ve panel birden bire
        // yetkili bölge sahibiymiş gibi davranmaya başlardı.
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var state = states.FirstOrDefault(item =>
            string.Equals(item.SiteName, parentSiteName, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            return new SubdomainRecordResult { ZoneMissing = true };
        }

        // ÜST BÖLGE İKİNCİLSE KAYIT YAZILAMAZ. Sonuç nesnesiyle bildiriliyor, fırlatılmıyor:
        // alt alan adı sitesinin kurulması bu yüzden yarıda kalmamalı — kullanıcı kaydı master'da
        // açacaktır, ama bunu BİLMESİ gerekir (sessiz sonuç, aranamayan bir arıza demektir).
        if (state.IsSecondary)
        {
            return new SubdomainRecordResult { Error = "zone_is_secondary" };
        }

        // ÜSTÜNE YAZMA. Etiket zaten bir yere (başka sunucu, CDN, doğrulama kaydı) işaret ediyor
        // olabilir; site açmak onu sessizce ele geçirmek için yeterli bir gerekçe değil.
        var mevcut = state.Records.FirstOrDefault(record =>
            string.Equals(record.Host, etiket, StringComparison.OrdinalIgnoreCase));
        var adres = await ResolvePreferredServerAddressAsync(cancellationToken);

        if (mevcut is not null)
        {
            // "Kayıt var" ile "kayıt YANLIŞ yeri gösteriyor" ayrı şeyler.
            //
            // Varsayılan bölge şablonu zaten www / mail / ftp / webmail / ns1 / ns2 kayıtlarını
            // oluşturuyor. Bu ayrım olmadan, biri "mail.ornek.com" sitesi açtığında panel kendi
            // doğru kaydı için uyarı basıyordu. Her şey yolundayken uyarı basmak, gerçek
            // uyarıların da okunmamasına yol açar.
            var zatenDogru = DnsSubdomainDecision.ZatenDogruMu(
                mevcut.Type, mevcut.Value, mevcut.IsManaged, adres, state.ZoneName);

            return new SubdomainRecordResult
            {
                AlreadyExists = true,
                AlreadyCorrect = zatenDogru,
                ExistingRecord = $"{mevcut.Type} {mevcut.Value}".Trim()
            };
        }

        if (string.IsNullOrWhiteSpace(adres))
        {
            // Adres ÖLÇÜLEMEDİ. Uydurma bir değer yazmak (127.0.0.1 gibi) alan adını çalışmaz
            // hâle getirir ve kullanıcı kaydın doğru olduğunu sanır.
            return new SubdomainRecordResult { Error = "server_address_unknown" };
        }

        var (_, yayinAnahtari) = await UpsertRecordCoreAsync(
            parentSiteName,
            new UpsertDnsRecordRequest { Type = "A", Host = etiket, Value = adres, Ttl = 3600 },
            cancellationToken);

        // "Panel durumuna yazıldı" ile "DNS'te var" AYRI şeylerdir ve ikisi de ayrı ayrı söylenir.
        // Eskiden yalnız birincisi ölçülüp ikincisi VARSAYILIYORDU: Windows DNS rolü yokken bile
        // site oluşturma ekranı "dns: blog.ornek.com A 1.2.3.4" yazıyordu.
        return new SubdomainRecordResult
        {
            Created = true,
            Address = adres,
            PublishWarningKey = yayinAnahtari
        };
    }

    public async Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        EnsureZoneIsWritable(state);
        var removed = state.Records.RemoveAll(item => string.Equals(item.Id, recordId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new AppException("dns.record_not_found", 404, 404);
        }

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        var zone = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            return;
        }

        states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);

        // Windows DNS'te bölge varsa best-effort kaldır (yoksa sessiz geç).
        if (!string.IsNullOrWhiteSpace(zone.ZoneName) && await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\n" +
                $"$zoneName = '{EscapePowerShellLiteral(zone.ZoneName)}'\n" +
                "if (Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue) { Remove-DnsServerZone -Name $zoneName -Force -ErrorAction SilentlyContinue }";
            try
            {
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
            catch
            {
                // best-effort: panel DNS kaydı zaten kaldırıldı.
            }
        }
    }

    private async Task<DnsZoneState> GetOrCreateStateAsync(SiteDto site, CancellationToken cancellationToken, bool persist = true)
    {
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var state = states.FirstOrDefault(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase));
        if (state is not null)
        {
            state.ZoneName = NormalizeZoneName(state.ZoneName);
            state.NameServers = NormalizeNameServers(state.NameServers, state.ZoneName);
            state.ZoneTransferIps = NormalizeZoneTransferIps(state.ZoneTransferIps?.ToArray());
            state.MasterServers = NormalizeIpList(state.MasterServers?.ToArray(), "dns.invalid_master_ip");
            state.Records = SortRecords(state.Records.Select(record => NormalizeRecord(record, state.ZoneName))).ToList();
            if (persist && state.DefaultsVersion < CurrentDefaultsVersion)
            {
                MergeMissingDefaults(state.Records, await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
                state.DefaultsVersion = CurrentDefaultsVersion;
                state.Records = SortRecords(state.Records).ToList();
                await UpsertStateAsync(state, cancellationToken);
                await TrySyncWindowsDnsAsync(state, cancellationToken);
            }

            return state;
        }

        var zoneName = ResolveDefaultZoneName(site);
        var defaultRecords = await BuildDefaultRecordsAsync(site, zoneName, serverAddress, cancellationToken);
        state = new DnsZoneState
        {
            SiteName = site.Name,
            ZoneName = zoneName,
            NameServers = NormalizeNameServers([], zoneName),
            Records = defaultRecords,
            DefaultsVersion = CurrentDefaultsVersion
        };
        if (persist)
        {
            await UpsertStateAsync(state, cancellationToken);
            await TrySyncWindowsDnsAsync(state, cancellationToken);
        }
        return state;
    }

    private async Task UpsertStateAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        states.RemoveAll(item => string.Equals(item.SiteName, state.SiteName, StringComparison.OrdinalIgnoreCase));
        states.Add(state);
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
    }

    private async Task<SiteDnsZoneDto> ToDtoAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var isDnsAvailable = await IsWindowsDnsAvailableAsync(cancellationToken);
        var windowsZoneType = isDnsAvailable
            ? await ReadWindowsZoneTypeAsync(state.ZoneName, cancellationToken)
            : string.Empty;
        var isZonePresent = windowsZoneType.Length > 0;

        // DNSSEC yalnız BİRİNCİL bölgede sorulur: ikincil bölgenin imzası master'a aittir, burada
        // ölçülen değer kullanıcıya "bu panelden yönetilebilir" izlenimi verirdi.
        var (isSigned, dsRecords) = isZonePresent && !state.IsSecondary
            ? await ReadDnssecAsync(state.ZoneName, cancellationToken)
            : (false, Array.Empty<string>());

        return new SiteDnsZoneDto
        {
            SiteName = state.SiteName,
            ZoneName = state.ZoneName,
            NameServers = state.NameServers,
            ZoneTransferIps = (state.ZoneTransferIps ?? []).ToArray(),
            IsSecondary = state.IsSecondary,
            MasterServers = (state.MasterServers ?? []).ToArray(),
            // İkincil bölgede NOTIFY/AXFR listesi anlamsızdır (bölgenin sahibi biz değiliz) → boş.
            EffectiveNotifyServers = state.IsSecondary
                ? []
                : MergeNotifyTargets(state.ZoneTransferIps, await ReadServerWideSecondariesAsync(cancellationToken)).ToArray(),
            WindowsZoneType = windowsZoneType,
            Records = SortRecords(state.Records).Select(ToDto).ToArray(),
            IsWindowsDnsAvailable = isDnsAvailable,
            IsWindowsZonePresent = isZonePresent,
            Source = isDnsAvailable ? "windows-dns" : "panel-managed",
            StatusMessage = ResolveStatusMessage(state, isDnsAvailable, isZonePresent, windowsZoneType),
            IsDnssecSigned = isSigned,
            DnssecDsRecords = dsRecords
        };
    }

    /// <summary>
    /// Bölgenin durum satırı. Panelin kaydı ile Windows'un gerçeği AYRILDIĞINDA bunu söyler.
    /// </summary>
    /// <remarks>
    /// Uyuşmazlığı ayrı bir mesajla bildirmek şart: panel "birincil" derken bölge Windows'ta
    /// "Secondary" ise her kayıt yazma denemesi başarısız olur ve kullanıcı sebebini bulamaz.
    /// Tersi de aynı derecede yanıltıcıdır — panel "ikincil" derken bölge birincil kalmışsa, alan
    /// adı master'daki güncel veriyi HİÇ almaz ama ekranda her şey doğru görünür.
    /// </remarks>
    private static string ResolveStatusMessage(DnsZoneState state, bool isDnsAvailable, bool isZonePresent, string windowsZoneType)
    {
        if (!isDnsAvailable)
        {
            return "dns.windows_dns_unavailable";
        }

        if (!isZonePresent)
        {
            return "dns.windows_zone_missing";
        }

        var windowsSecondary = string.Equals(windowsZoneType, "Secondary", StringComparison.OrdinalIgnoreCase);
        if (windowsSecondary != state.IsSecondary)
        {
            return "dns.zone_type_mismatch";
        }

        return state.IsSecondary ? "dns.secondary_zone_active" : "dns.windows_zone_present";
    }

    private static SiteDnsRecordDto ToDto(DnsRecordState state)
    {
        return new SiteDnsRecordDto
        {
            Id = state.Id,
            Type = state.Type,
            Host = state.Host,
            Value = state.Value,
            Ttl = state.Ttl,
            Priority = state.Priority,
            IsManaged = state.IsManaged
        };
    }

    // --- DNS sunucusu erişilebilirlik sağlığı + onarım ---
    // KANITLANMIŞ OLAY (2026-07-23 reboot, 6 gün fark edilmedi): DNS servisi "Running", yapılandırmada
    // genel IP dinleme listesinde, AMA ağ arayüzü hazır olmadan başladığı için fiilen yalnız
    // 127.0.0.1/::1/link-local'e bind olmuş. Dışarıdan tek bir sorgu bile yanıtlanmadı → siteler
    // açılmadı, Let's Encrypt DNS-01 zaman aşımına uğradı. Bu yüzden SERVİS DURUMU DEĞİL,
    // 53 portunun TCP ve UDP dinleyicileri Windows IP Helper API ile DNS PID'ine göre ölçülür.
    private const string ServerHealthScript = """
        $ErrorActionPreference = 'Stop'
        $installed = [bool](Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)
        $zones = 0
        if ($installed) {
            $zones = @(Get-DnsServerZone -ErrorAction Stop | Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' }).Count
        }
        @{ installed = $installed; zoneCount = $zones } | ConvertTo-Json -Compress
        """;

    private async Task<ServerHealthProbe> ReadServerProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = WindowsNetworkHealth.ReadService("DNS");
        if (service.Status == "NotInstalled")
            return new ServerHealthProbe { ServiceStatus = "NotInstalled" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ServerHealthProbe? metadata;
        try
        {
            metadata = new ServerHealthProbe { Installed = true, ZoneCount = await WindowsDnsInspection.ReadPrimaryZoneCountAsync(deadline.Token) };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !deadline.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Native DNS inventory failed; using PowerShell fallback.");
            var run = await ProcessRunner.RunPowerShellStrictAsync(ServerHealthScript, deadline.Token);
            metadata = run.Success ? ParseProbe(run.StandardOutput) : null;
        }
        if (metadata is null) throw new IOException("DNS zone inventory could not be measured.");
        // Read after the inventory, so a concurrent service restart cannot leave us using an old PID.
        service = WindowsNetworkHealth.ReadService("DNS");
        var listeners = WindowsNetworkHealth.ReadListeners(service.ProcessId, 53);
        return new ServerHealthProbe
        {
            Installed = metadata.Installed,
            ServiceStatus = service.Status,
            StartMode = service.StartMode,
            DelayedAutoStart = service.Delayed,
            Listening = listeners.Tcp.Concat(listeners.Udp).Distinct().ToArray(),
            TcpListening = listeners.Tcp,
            UdpListening = listeners.Udp,
            Expected = WindowsNetworkHealth.ReadLocalIPv4(),
            ZoneCount = metadata.ZoneCount
        };
    }
    private sealed class DnsWatchdogRecord
    {
        public DateTime? LastCheckUtc { get; set; }

        public DateTime? LastRepairUtc { get; set; }

        public string LastStatus { get; set; } = string.Empty;

        /// <summary>
        /// Son onarım tuttu mu. `LastStatus` anahtarının YANINDA duruyor, onun yerine değil:
        /// anahtar kullanıcıya NE olduğunu söyler, bu alan arayüze hangi RENGİ çizeceğini.
        /// Arayüzün anahtar dizgesini koklayarak renk seçmesi, anahtar bir gün değiştiğinde
        /// sessizce bozulurdu. Bu alanı tanımayan ESKİ nöbetçi kaydında `null` kalır ve
        /// "ölçülmedi" anlamına gelir — geçmiş kayıt yanlış renge boyanmaz.
        /// </summary>
        public bool? LastRepairOk { get; set; }
    }

    private sealed class ServerHealthProbe
    {
        public bool Installed { get; init; }

        public string ServiceStatus { get; init; } = "Unknown";

        public string StartMode { get; init; } = "Unknown";

        public bool DelayedAutoStart { get; init; }

        public string[] Listening { get; init; } = [];
        public string[] TcpListening { get; init; } = [];
        public string[] UdpListening { get; init; } = [];

        public string[] Expected { get; init; } = [];

        public int ZoneCount { get; init; }

        public string[] Notes { get; init; } = [];
    }

    /// <summary>
    /// Saf karar mantığı (test edilebilir): 53'te dinlenen adresler, beklenen genel adreslerden
    /// hangilerini karşılıyor. <c>0.0.0.0</c> / <c>::</c> tüm arayüzleri kapsar.
    /// </summary>
    internal static (bool PubliclyBound, string[] Missing) EvaluateBinding(
        IEnumerable<string> listeningAddresses,
        IEnumerable<string> expectedAddresses)
    {
        var listening = listeningAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .ToArray();

        var expected = expectedAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var wildcard = listening.Any(address => address is "0.0.0.0" or "::" or "*");
        if (wildcard)
        {
            return (true, []);
        }

        var missing = expected
            .Where(address => !listening.Contains(address, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // Beklenen adres hiç bulunamadıysa (ör. NAT arkasında tek arayüz okunamadı) loopback
        // DIŞINDA bir adreste dinliyor olmak yeterli sayılır; yanlış alarm üretmeyiz.
        var bound = expected.Length > 0
            ? missing.Length < expected.Length
            : listening.Any(address => !IsLoopbackOrLinkLocal(address));

        return (bound, missing);
    }

    private static bool IsLoopbackOrLinkLocal(string address)
    {
        return address.StartsWith("127.", StringComparison.Ordinal) ||
               address.StartsWith("169.254.", StringComparison.Ordinal) ||
               address is "::1" ||
               address.StartsWith("fe80", StringComparison.OrdinalIgnoreCase);
    }

    internal static (bool PubliclyBound, string[] Missing) EvaluateTransports(
        string[] tcpListeners, string[] udpListeners, string[] expected)
    {
        var tcp = EvaluateBinding(tcpListeners, expected);
        var udp = EvaluateBinding(udpListeners, expected);
        var candidates = expected.Length > 0 ? expected : tcpListeners.Concat(udpListeners)
            .Where(address => !IsLoopbackOrLinkLocal(address)).Distinct().ToArray();
        var common = candidates.Any(address => EvaluateBinding(tcpListeners, [address]).Missing.Length == 0 &&
            EvaluateBinding(udpListeners, [address]).Missing.Length == 0);
        return (tcp.PubliclyBound && udp.PubliclyBound && common,
            tcp.Missing.Concat(udp.Missing).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default)
    {
        var watchdog = await fileStore.ReadAsync(WatchdogPath, new DnsWatchdogRecord(), cancellationToken);
        ServerHealthProbe probe;
        try { probe = await ReadServerProbeAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "DNS server health could not be measured.");
            return new DnsServerHealthDto
            {
                LastAutoRepairUtc = watchdog.LastRepairUtc,
                LastAutoRepairStatus = watchdog.LastStatus,
                LastAutoRepairSucceeded = watchdog.LastRepairOk,
                // Ayrıntılı istisna sunucu günlüğündedir; ölçüm hatası kurulu değil anlamına gelmez.
                Details = "dns.health_unmeasurable"
            };
        }

        var expected = probe.Expected.ToList();

        // Panelin kayıtlı sunucu adresi bir IP ise beklenenlere ekle: NAT arkasında yerel arayüz
        // adresi farklı olabilir, ama panel hangi adresi yayınladığını bilir.
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        if (System.Net.IPAddress.TryParse(serverAddress, out _) &&
            !expected.Contains(serverAddress, StringComparer.OrdinalIgnoreCase))
        {
            expected.Add(serverAddress);
        }

        var (publiclyBound, missing) = EvaluateTransports(probe.TcpListening, probe.UdpListening, expected.ToArray());

        var details = new StringBuilder()
            .Append("service=").Append(probe.ServiceStatus)
            .Append("; startMode=").Append(probe.StartMode)
            .Append(probe.DelayedAutoStart ? " (delayed-auto)" : string.Empty)
            .Append("; listening=").Append(probe.Listening.Length == 0 ? "(none)" : string.Join(",", probe.Listening))
            .Append("; tcp=").Append(string.Join(",", probe.TcpListening))
            .Append("; udp=").Append(string.Join(",", probe.UdpListening))
            .Append("; expected=").Append(expected.Count == 0 ? "(none)" : string.Join(",", expected))
            .Append("; zones=").Append(probe.ZoneCount);

        if (probe.Notes.Length > 0)
        {
            details.Append("; notes=").Append(string.Join(" | ", probe.Notes.Take(6)));
        }

        return new DnsServerHealthDto
        {
            Installed = probe.Installed,
            ServiceStatus = probe.ServiceStatus,
            StartMode = probe.StartMode,
            DelayedAutoStart = probe.DelayedAutoStart,
            ListeningAddresses = probe.Listening,
            ExpectedAddresses = expected.ToArray(),
            MissingAddresses = missing,
            PubliclyBound = publiclyBound,
            ZoneCount = probe.ZoneCount,
            LastAutoRepairUtc = watchdog.LastRepairUtc,
            LastAutoRepairStatus = watchdog.LastStatus,
            LastAutoRepairSucceeded = watchdog.LastRepairOk,
            Details = details.ToString()
        };
    }

    public async Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default)
    {
        // Otomasyon worker'ı dakikada bir tetiklenir; ölçüm aralığını BURADA kısıtlıyoruz (servis
        // Scoped olduğu için bellekte sayaç tutulamaz, durum dosyada). Zaman damgası ölçümden ÖNCE
        // yazılır: ölçüm patlarsa dakika başı yeniden denenip sıcak döngü oluşmaz.
        var watchdog = await fileStore.ReadAsync(WatchdogPath, new DnsWatchdogRecord(), cancellationToken);
        if (watchdog.LastCheckUtc is { } lastCheck && DateTime.UtcNow - lastCheck < WatchdogInterval)
        {
            return null;
        }

        watchdog.LastCheckUtc = DateTime.UtcNow;
        await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);

        var health = await GetServerHealthAsync(cancellationToken);
        if (!health.NeedsRepair)
        {
            return health;
        }

        if (watchdog.LastRepairUtc is { } last && DateTime.UtcNow - last < WatchdogCooldown)
        {
            // Soğuma süresi: art arda yeniden başlatma döngüsü kurmayız. Sorun devam ediyorsa
            // Sunucu Sağlığı ekranında kırmızı olarak görünmeye devam eder.
            return health;
        }

        try
        {
            var repaired = await RepairServerAsync(true, cancellationToken);
            watchdog.LastRepairUtc = DateTime.UtcNow;

            // ANAHTAR + AYRINTI, sabit Turkce cumle DEGIL.
            //
            // OLCULEN HATA: bu alan (`DnsServerHealthDto.LastAutoRepairStatus`) Sunucu Sagligi
            // ekraninda HAM basiliyor (`ServerHealthView.vue`: `{{ dns.lastAutoRepairStatus }}`),
            // yani "ok: DNS servisi yeniden baslatildi..." cumlesini Ingilizce panelde de aynen
            // goruyordu. Basindaki "ok:"/"failed:" oneki de kimsenin okumadigi bir makine
            // isaretiydi; durumu zaten cumlenin kendisi soyluyor.
            //
            // Ayirici " — ": arayuzun `splitCompositeMessage`'i TAM OLARAK bu bicimi ayirir
            // (anahtar + serbest teshis metni). Teshis kismi (`repaired.Details`,
            // `exception.Message`) BILEREK cevrilmez: icinde `service=...; listening=...` gibi
            // olculmus deger tasir, cevrilecek bir cumle degildir.
            //
            // Dosyada DURAN eski Turkce degerler bozulmaz: arayuz anahtar bicimine uymayan
            // dizgeyi oldugu gibi basar, yani gecmis kayit okunakli kalir.
            watchdog.LastStatus = repaired.PubliclyBound
                ? "dns.watchdog.repaired"
                : $"dns.watchdog.still_unbound — {repaired.Details}";
            // Renk kararı da anahtarla BİRLİKTE yazılır: "yeniden başlattım ama hâlâ genel
            // adreste dinlemiyor" bir BAŞARISIZLIKTIR. Ekranda nötr gri yazıldığı sürece
            // kullanıcı bunu "onarım yapıldı" diye okuyordu.
            watchdog.LastRepairOk = repaired.PubliclyBound;
            await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);
        }
        catch (Exception exception)
        {
            watchdog.LastRepairUtc = DateTime.UtcNow;
            watchdog.LastStatus = $"dns.watchdog.repair_failed — {exception.Message}";
            watchdog.LastRepairOk = false;
            await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);
            throw;
        }

        // Nöbetçi kaydı yazıldıktan sonra yeniden ölç: dönen DTO hem güncel bind durumunu hem de
        // "son otomatik onarım" bilgisini içerir (Sunucu Sağlığı ekranı bunu gösterir).
        return await GetServerHealthAsync(cancellationToken);
    }

    public async Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default)
    {
        var before = await GetServerHealthAsync(cancellationToken);
        if (before.ServiceStatus == "Unknown")
            throw new AppException("dns.health_unmeasurable", 503, 503);
        if (!before.Installed)
        {
            throw new AppException("dns.server_not_installed", 409, 409, before.Details);
        }

        // sc.exe kullanılıyor: Windows PowerShell 5.1'in Set-Service'i AutomaticDelayedStart'ı
        // DESTEKLEMEZ. Bu, canlı sunucuda kanıtlanmış komutun aynısıdır.
        var script = new StringBuilder();
        if (applyDelayedAutoStart)
        {
            script.AppendLine("& sc.exe config DNS start= delayed-auto | Out-Null");
        }

        script.AppendLine("Restart-Service -Name 'DNS' -Force -ErrorAction Stop");
        // Servis ayağa kalkıp soketleri açana kadar kısa bekleme; hemen ölçersek dinleyiciler boş görünür.
        script.AppendLine("Start-Sleep -Seconds 3");

        var result = await ProcessRunner.RunPowerShellAsync(script.ToString(), cancellationToken);
        if (!result.Success)
        {
            throw new AppException("dns.server_restart_failed", 500, 500, BuildCommandDetails(result));
        }

        return await GetServerHealthAsync(cancellationToken);
    }

    private static ServerHealthProbe? ParseProbe(string standardOutput)
    {
        try
        {
            var text = (standardOutput ?? string.Empty).Trim();
            var start = text.IndexOf('{');
            if (start < 0)
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<ServerHealthProbe>(
                text[start..],
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    public async Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default)
    {
        var saved = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);

        // KAYIT LİSTESİ BOŞSA yerleşik şablona düşülür — ama ikincil DNS sunucu listesi KAYITLI
        // OLANDAN alınır. Aksi hâlde kayıt satırlarını tek tek silen bir yönetici, farkında olmadan
        // sunucu-geneli ikincil DNS yapılandırmasını da silmiş olurdu; yedek DNS sessizce beslenmeyi
        // bırakır ve bunu ancak birincil düştüğünde fark ederdi.
        var template = saved.Records.Count > 0 ? saved : BuiltInTemplate();
        template.SecondaryDnsServers = NormalizeIpList(saved.SecondaryDnsServers?.ToArray(), "dns.invalid_secondary_ip");
        return template;
    }

    public async Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA"
        };

        // Sunucu-geneli ikincil DNS sunucuları: geçersiz IP kaydı SESSİZCE ATLANMAZ, 400 döner.
        // Kayıt satırlarında atlama makul (bir satır bozuksa şablonun geri kalanı çalışır); burada
        // değil — "ikincil DNS tanımladım" diyen yönetici, IP'nin düşürüldüğünü fark edemezdi.
        var clean = new DnsTemplateDto
        {
            SecondaryDnsServers = NormalizeIpList(request.SecondaryDnsServers?.ToArray(), "dns.invalid_secondary_ip")
        };

        foreach (var record in request.Records ?? [])
        {
            var type = (record.Type ?? string.Empty).Trim().ToUpperInvariant();
            var host = (record.Host ?? string.Empty).Trim();
            var value = (record.Value ?? string.Empty).Trim();
            if (!allowed.Contains(type) || host.Length == 0 || value.Length == 0)
            {
                continue;
            }

            clean.Records.Add(new DnsTemplateRecordDto
            {
                Type = type,
                Host = host,
                Value = value,
                Ttl = record.Ttl > 0 ? record.Ttl : 3600,
                // MX önceliksiz kaydedilirse posta yönlendirmesi bozulur; istemci göndermezse 10'a düşer.
                Priority = type switch
                {
                    "MX" => record.Priority is > 0 ? record.Priority : 10,
                    "SRV" => record.Priority,
                    _ => null
                }
            });
        }

        await fileStore.WriteAsync(TemplatePath, clean, cancellationToken);

        // Dönen görünüm de aynı kuralı uygular: kayıtlar boşsa yerleşik set gösterilir, ama ikincil
        // DNS listesi KAYDEDİLEN listedir (yerleşik şablonda böyle bir liste yoktur).
        var effective = clean.Records.Count > 0 ? clean : BuiltInTemplate();
        effective.SecondaryDnsServers = clean.SecondaryDnsServers;
        return effective;
    }

    public async Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        // İKİNCİL BÖLGE İMZALANAMAZ: imza bölgenin sahibine, yani master'a aittir. Windows da
        // reddeder; denemek yerine sebebi söylemek kullanıcıyı master'a yönlendirir.
        EnsureZoneIsWritable(state);

        // DNSSEC, Windows DNS Server zone-imzalama gerektirir (Plesk-on-Windows ile aynı). Yoksa net hata.
        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            throw new AppException("dns.dnssec_requires_windows", 409, 409);
        }

        // İmzalamak için bölgenin Windows DNS'te bulunması gerekir → önce yayınla (kayıtlarıyla).
        await TrySyncWindowsDnsAsync(state, cancellationToken);

        var zone = EscapePowerShellLiteral(state.ZoneName);
        var script = enabled
            ? $"$ErrorActionPreference='Stop'; Invoke-DnsServerZoneSign -ZoneName '{zone}' -SignWithDefault -Force | Out-Null"
            : $"$ErrorActionPreference='Stop'; Invoke-DnsServerZoneUnsign -ZoneName '{zone}' -Force | Out-Null";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                enabled ? "dns.dnssec_sign_failed" : "dns.dnssec_unsign_failed",
                500,
                500,
                BuildCommandDetails(result));
        }

        return await ToDtoAsync(state, cancellationToken);
    }

    // Bölgenin DNSSEC durumunu + registrar'a verilecek DS kayıtlarını Windows DNS'ten okur. IsSigned + DS
    // (Export-DnsServerDnsSecPublicKey, SHA-256). DS alınamazsa boş döner (imza durumu yine doğru; UI ipucu verir). Best-effort.
    private async Task<(bool IsSigned, string[] DsRecords)> ReadDnssecAsync(string zoneName, CancellationToken cancellationToken)
    {
        var zone = EscapePowerShellLiteral(zoneName);
        var script = string.Join("\n", new[]
        {
            "$ErrorActionPreference='SilentlyContinue'",
            $"$z = Get-DnsServerZone -Name '{zone}'",
            "if (-not $z) { 'SIGNED=False'; return }",
            "'SIGNED=' + [bool]$z.IsSigned",
            "if ($z.IsSigned) {",
            $"  $keys = Export-DnsServerDnsSecPublicKey -ZoneName '{zone}' -Path $env:TEMP -PassThru -DigestType Sha256 -ErrorAction SilentlyContinue",
            "  foreach ($k in $keys) {",
            "    $rd = $k.RecordData",
            "    if ($rd -and ($rd.PSObject.Properties.Name -contains 'KeyTag') -and ($rd.PSObject.Properties.Name -contains 'Digest')) {",
            "      'DS=' + $k.HostName + ' IN DS ' + $rd.KeyTag + ' ' + [int]$rd.CryptographicAlgorithm + ' ' + [int]$rd.DigestType + ' ' + $rd.Digest",
            "    }",
            "  }",
            "}"
        });

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            return (false, Array.Empty<string>());
        }

        var lines = result.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var isSigned = lines.Any(line => line.Equals("SIGNED=True", StringComparison.OrdinalIgnoreCase));
        var dsRecords = lines
            .Where(line => line.StartsWith("DS=", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .Where(line => line.Length > 0)
            .Distinct()
            .ToArray();

        return (isSigned, dsRecords);
    }

    // Kayıtlı şablon yoksa UI'de gösterilecek/uygulanacak yerleşik varsayılan (mevcut CreateDefaultRecords ile aynı set, yer tutuculu).
    // Yeni bölgenin varsayılan kayıtları. POSTA KAYITLARI DA BURADA olmalı: panel kendi mail
    // sunucusunu (Stalwart) ve webmail'i (webmail.<alan>) kuruyor, ama bölgede MX/SPF/DMARC yoksa
    // o alan adına posta HİÇ ULAŞMAZ, giden posta da spam'e düşer; webmail adresi de çözülmez.
    // (Karşılaştırma: Plesk'in ürettiği bölgede MX, mail A, webmail A, SPF ve DMARC standarttır.)
    // DKIM burada YOK: anahtar mail domaini oluşturulunca üretilir → EnsureMailRecordsAsync ekler.
    internal static DnsTemplateDto BuiltInTemplate() => new()
    {
        Records =
        [
            new DnsTemplateRecordDto { Type = "NS", Host = "@", Value = "{ns1}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "NS", Host = "@", Value = "{ns2}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "@", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "ns1", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "ns2", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "CNAME", Host = "www", Value = "{domain}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "CNAME", Host = "ftp", Value = "{domain}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "mail", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "webmail", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "MX", Host = "@", Value = "mail.{domain}", Ttl = 3600, Priority = 10 },
            new DnsTemplateRecordDto { Type = "TXT", Host = "@", Value = "v=spf1 mx a ip4:{ip} ~all", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "TXT", Host = "_dmarc", Value = "v=DMARC1; p=quarantine; rua=mailto:postmaster@{domain}", Ttl = 3600 },

            // ISTEMCI OTOMATIK YAPILANDIRMA (RFC 6186). Thunderbird, Apple Mail, K-9 ve daha bircok
            // istemci bu kayitlari okuyup sunucu/port/sifreleme ayarlarini KENDISI doldurur; kullanici
            // yalniz e-posta ve parola yazar. Plesk'in otomatik yapilandirma ozelliginin DNS ayagi budur.
            //
            // Neden bu bicim: SRV degeri "<agirlik> <port> <hedef>", oncelik ayri alanda.
            // 465 = ortuk TLS, 587 = STARTTLS; ikisi de duyurulur ki istemci hangisini
            // destekliyorsa onu secsin. Port 25 BILEREK duyurulmaz: o MTA'lar arasidir,
            // istemci gonderimi icin kullanilmamalidir.
            new DnsTemplateRecordDto { Type = "SRV", Host = "_submissions._tcp", Value = "1 465 mail.{domain}", Ttl = 3600, Priority = 0 },
            new DnsTemplateRecordDto { Type = "SRV", Host = "_submission._tcp", Value = "1 587 mail.{domain}", Ttl = 3600, Priority = 10 },
            new DnsTemplateRecordDto { Type = "SRV", Host = "_imaps._tcp", Value = "1 993 mail.{domain}", Ttl = 3600, Priority = 0 },
            new DnsTemplateRecordDto { Type = "SRV", Host = "_pop3s._tcp", Value = "1 995 mail.{domain}", Ttl = 3600, Priority = 10 },

            // TLS RAPORLAMA (RFC 8460): baska sunucular bu alan adina posta gonderirken TLS
            // kurulamazsa gunluk ozet raporu buraya yollar. Tamamen DNS tabanlidir, ek bir servis
            // gerektirmez -- bu yuzden MTA-STS'ten farkli olarak tek basina calisir.
            new DnsTemplateRecordDto { Type = "TXT", Host = "_smtp._tls", Value = "v=TLSRPTv1; rua=mailto:postmaster@{domain}", Ttl = 3600 }
        ]
    };

    // Yeni bölgenin yönetilen (managed) varsayılan kayıtları: kayıtlı şablon varsa onu yer tutucularla genişletir,
    // yoksa yerleşik CreateDefaultRecords'a düşer. Şablon tüm kayıtları atlanırsa da yerleşiğe düşer (boş zone olmaz).
    private async Task<List<DnsRecordState>> BuildDefaultRecordsAsync(SiteDto site, string zoneName, string serverAddress, CancellationToken cancellationToken)
    {
        var template = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
        if (template.Records.Count == 0)
        {
            return CreateDefaultRecords(site, zoneName, serverAddress).ToList();
        }

        // KAYITLI SABLON, YERLESIK SABLONU TAMAMEN GOLGELEMEZ.
        //
        // Eskiden kayitli sablon varsa YALNIZCA o kullaniliyordu. Sonuc: panelin yeni surumleriyle
        // gelen varsayilanlar (istemci otomatik yapilandirma SRV kayitlari, TLS raporlama) sablonu
        // bir kez kaydetmis kurulumlara HIC ULASMIYORDU -- ve panel kendi sablonuna gore "eksik
        // yok" dedigi icin bu sessizce oluyordu. Canlida tam olarak bu oldu: kayitlar aylarca
        // eksikti, hicbir uyari cikmadi.
        //
        // Birlestirme TUR|KONAK ikilisine gore: kullanicinin sablonda ozellestirdigi bir kayit
        // (ornegin kendi MX'i) KORUNUR, yalnizca sablonda hic bulunmayan yeni varsayilanlar eklenir.
        var mevcutAnahtarlar = template.Records
            .Select(record => $"{record.Type}|{record.Host}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var eksikVarsayilanlar = BuiltInTemplate().Records
            .Where(record => !mevcutAnahtarlar.Contains($"{record.Type}|{record.Host}"))
            .ToList();

        if (eksikVarsayilanlar.Count > 0)
        {
            template = new DnsTemplateDto { Records = [.. template.Records, .. eksikVarsayilanlar] };
        }

        var ip = ResolveDefaultIpv4Address(site, serverAddress);
        var ns1 = $"ns1.{zoneName}";
        var ns2 = $"ns2.{zoneName}";
        var records = new List<DnsRecordState>();
        foreach (var tpl in template.Records)
        {
            var host = ExpandPlaceholders(tpl.Host, zoneName, ip, ns1, ns2);
            var value = ExpandPlaceholders(tpl.Value, zoneName, ip, ns1, ns2);
            // {ip} çözülemediyse (sunucu IPv4 yok) A/AAAA gibi kayıtlar boş value'lu olur → atla (geçersiz kayıt yazma).
            if (host.Length == 0 || value.Length == 0)
            {
                continue;
            }

            records.Add(new DnsRecordState
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = tpl.Type,
                Host = host,
                Value = value,
                Ttl = tpl.Ttl > 0 ? tpl.Ttl : 3600,
                // MX önceliği şablondan taşınmalı; taşınmazsa MX kaydı önceliksiz yazılır.
                Priority = tpl.Priority,
                IsManaged = true
            });
        }

        return records.Count > 0 ? records : CreateDefaultRecords(site, zoneName, serverAddress).ToList();
    }

    private static string ExpandPlaceholders(string input, string zone, string ip, string ns1, string ns2)
    {
        var text = input ?? string.Empty;

        // BICIMSIZ SPF NOBETCISI: IPv4 cozulemediginde (ip == "") "ip4:{ip}" -> "ip4:" olur ve
        // "v=spf1 mx a ip4: ~all" gibi GECERSIZ (permerror) bir SPF yazilirdi. Bu, ayni SPF'i
        // dogru sekilde "v=spf1 mx a ~all"e indiren MailService/CreateDefaultRecords'tan sessizce
        // ayrisiyordu. ip bosken " ip4:{ip}" belirtecini tumden dusur; boylece sablon yolu da
        // ayni tek kaynakla ayni ciktiyi verir. Kullanicinin kendi ip4:/include: mekanizmalari
        // ("{ip}" icermezler) ETKILENMEZ.
        if (ip.Length == 0)
        {
            text = text.Replace(" ip4:{ip}", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return text
            .Replace("{domain}", zone, StringComparison.OrdinalIgnoreCase)
            .Replace("{ip}", ip, StringComparison.OrdinalIgnoreCase)
            .Replace("{ns1}", ns1, StringComparison.OrdinalIgnoreCase)
            .Replace("{ns2}", ns2, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static IEnumerable<DnsRecordState> CreateDefaultRecords(SiteDto site, string zoneName, string preferredServerAddress)
    {
        var serverIp = ResolveDefaultIpv4Address(site, preferredServerAddress);
        yield return CreateManagedRecord("NS", "@", $"ns1.{zoneName}");
        yield return CreateManagedRecord("NS", "@", $"ns2.{zoneName}");
        yield return CreateManagedRecord("CNAME", "www", zoneName);
        yield return CreateManagedRecord("CNAME", "ftp", zoneName);

        // Posta: MX + SPF + DMARC olmadan alan adına posta ULAŞMAZ, giden posta spam'e düşer.
        yield return CreateManagedRecord("MX", "@", $"mail.{zoneName}", priority: 10);
        yield return CreateManagedRecord("TXT", "_dmarc", $"v=DMARC1; p=quarantine; rua=mailto:postmaster@{zoneName}");

        if (!string.IsNullOrWhiteSpace(serverIp))
        {
            yield return CreateManagedRecord("A", "@", serverIp);
            yield return CreateManagedRecord("A", "ns1", serverIp);
            yield return CreateManagedRecord("A", "ns2", serverIp);
            yield return CreateManagedRecord("A", "mail", serverIp);
            yield return CreateManagedRecord("A", "webmail", serverIp);
        }

        // SPF TEK KAYNAKTAN: MailService ile ayni kural (Mail.SpfPolicy.Build). serverIp bos ise
        // ip4 mekanizmasi yazilmaz -> "v=spf1 mx a ~all" (bicimsiz "ip4:" uretilmez).
        yield return CreateManagedRecord("TXT", "@", Mail.SpfPolicy.Build(serverIp));
    }

    private static DnsRecordState CreateManagedRecord(string type, string host, string value, int? priority = null)
    {
        return new DnsRecordState
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Host = host,
            Value = value,
            Ttl = 3600,
            Priority = priority,
            IsManaged = true
        };
    }

    private static void MergeMissingDefaults(List<DnsRecordState> records, IEnumerable<DnsRecordState> defaults)
    {
        foreach (var defaultRecord in defaults)
        {
            var exists = records.Any(record =>
                string.Equals(record.Type, defaultRecord.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Host, defaultRecord.Host, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                records.Add(defaultRecord);
            }
        }
    }

    private static string ResolveDefaultIpv4Address(SiteDto site, string preferredServerAddress)
    {
        var explicitIp = site.Bindings
            .Select(binding => binding.IpAddress)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value != "*" &&
                IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address));

        if (!string.IsNullOrWhiteSpace(explicitIp))
        {
            return explicitIp;
        }

        if (IPAddress.TryParse(preferredServerAddress, out var preferredAddress) &&
            preferredAddress.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(preferredAddress))
        {
            return preferredAddress.ToString();
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.ToString())
            .FirstOrDefault() ?? string.Empty;
    }

    private async Task<string> ResolvePreferredServerAddressAsync(CancellationToken cancellationToken)
    {
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        return preferences.ServerAddress;
    }

    /// <summary>
    /// Sunucu-geneli ikincil (slave) DNS sunucularının IP'leri; şablondan okunur. Okuma başarısız
    /// olursa BOŞ döner: senkronu bir yapılandırma dosyası yüzünden komple durdurmak, bölgeyi
    /// güncellenemez hâlde bırakırdı.
    /// </summary>
    private async Task<List<string>> ReadServerWideSecondariesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var template = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
            return NormalizeIpList(template.SecondaryDnsServers?.ToArray(), "dns.invalid_secondary_ip");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sunucu-geneli ikincil DNS sunucu listesi okunamadi; bolgeye ozel liste kullanilacak.");
            return [];
        }
    }

    /// <summary>
    /// Bölgeyi Windows DNS'e yayınlar.
    /// </summary>
    /// <returns>
    /// Yayınlandıysa BOŞ dizge; yayınlanmadıysa SEBEBİNİ söyleyen mesaj anahtarı.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA — burası hiçbir iz bırakmadan dönüyordu.</b> Windows DNS rolü kurulu
    /// olmadığında metot log, istisna ya da dönüş değeri OLMADAN <c>return</c> ediyordu. Dokuz
    /// çağıranın hepsi işlemi başarılı sayıp 200 + <c>dns.record_saved</c> döndürüyordu; en
    /// görünür sonucu site oluşturma ekranının <c>"dns: blog.ornek.com A 1.2.3.4"</c> satırıydı —
    /// DNS'te hiçbir şey yokken "oluşturuldu" diyen bir satır. Panelin en pahalı hata sınıfı tam
    /// olarak budur: yapılmamış işi yapılmış saymak (aynı cümle <see cref="EnsureZoneIsWritable"/>
    /// içinde de yazılı; oradaki ders bu yola hiç uygulanmamıştı).
    /// </para>
    /// <para>
    /// <b>Neden istisna DEĞİL, mesaj anahtarı.</b> DNS rolünün kurulu olmaması bir ARIZA değil bir
    /// OLGUDUR: müşteri alan adının DNS'ini Cloudflare'de tutuyor olabilir. Fırlatmak, posta kutusu
    /// açma / alan adı tazeleme / site kurma gibi bu yolu ÇAĞIRAN akışları DNS yüzünden
    /// durdururdu — <see cref="UpsertRecordsAsync"/> ikincil bölge için tam olarak bu gerekçeyle
    /// fırlatmıyor. Anahtar döndürmek çağırana "söyle ama durdurma" imkânı verir; arayüz onu
    /// <c>t()</c> ile çevirir.
    /// </para>
    /// <para>
    /// Panel durumu YİNE kaydedilir: bölge birincile döndüğünde / rol kurulduğunda yeniden
    /// yayınlanır ve <see cref="GetPublishedRecordKeysAsync"/> denetimi bu kayıtları eksik gösterir.
    /// </para>
    /// </remarks>
    private async Task<string> TrySyncWindowsDnsAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            // Bilgi seviyesi, uyarı DEĞİL: rolün kurulu olmaması beklenen bir kurulum biçimidir.
            // Sessiz kalmıyoruz ama kullanıcıyı da kırmızıya boğmuyoruz.
            logger.LogInformation(
                "Windows DNS Server rolu yok; bolge panelde saklandi ama YAYINLANMADI ({Zone}).",
                state.ZoneName);
            return NotPublishedNoServerKey;
        }

        // IKINCIL BOLGE: yalnizca kip + master listesi uygulanir, KAYIT YAZILMAZ.
        //
        // Kayit yazmaya calismak yalnizca hata vermekle kalmazdi: panelin kayit listesi ile
        // bolgenin gercek icerigi (master'dan gelen) farkli oldugu icin, "yayinlanan gruplar"
        // listesi de yalan soylemeye baslar ve bir sonraki birincil senkronda master'dan gelen
        // kayitlar "oksuz" sayilip silinmeye calisilirdi.
        if (state.IsSecondary)
        {
            // MASTER'SIZ IKINCIL BOLGEYE DOKUNULMAZ.
            //
            // Bu duruma yalnizca durum dosyasi elle bozulunca dusulur (SetZoneModeAsync en az bir
            // master sart kosar). Boyle bir durumda bolgeyi birincile cevirmek panel kayitlarini
            // slave bir bolgenin uzerine yazardi; hicbir sey yapmamak tek guvenli davranistir.
            if (state.MasterServers is not { Count: > 0 })
            {
                logger.LogWarning(
                    "Bolge ikincil isaretli ama master listesi BOS ({Zone}); Windows DNS'e dokunulmadi.",
                    state.ZoneName);
                return NotPublishedSecondaryNoMasterKey;
            }

            var secondaryResult = await ProcessRunner.RunPowerShellAsync(
                BuildSecondaryZoneScript(state.ZoneName, state.MasterServers),
                cancellationToken);

            if (!secondaryResult.Success)
            {
                throw new AppException("dns.secondary_apply_failed", 500, 500, BuildCommandDetails(secondaryResult));
            }

            return string.Empty;
        }

        var script = BuildSyncScript(state, MergeNotifyTargets(state.ZoneTransferIps, await ReadServerWideSecondariesAsync(cancellationToken)));
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            // Script kendi hazirladigi OZET satirini birakiyorsa onu kullan: tam script dokumu
            // yerine "hangi kayit, neden" bilgisini gostermek, sorunu aramayi degil GORMEYI saglar.
            var ozet = result.StandardOutput
                .Split(NewLineSeparators, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("ADAPANEL-FAIL:", StringComparison.Ordinal));

            throw new AppException(
                "dns.sync_failed",
                500,
                500,
                ozet is null ? BuildCommandDetails(result) : ozet["ADAPANEL-FAIL:".Length..].Trim());
        }

        // UZUN TXT KAYITLARI: ana senkron bittikten ve bolge kilidi birakildiktan SONRA,
        // her biri KENDI PowerShell turunda yazilir. Ayni script icinde yapmak bolgeyi
        // kilitleyip diger kayitlari dusuruyordu.
        await WriteLongTxtRecordsAsync(state, cancellationToken);

        // Yazma BASARILI olduktan sonra kaydedilir: basarisiz bir senkrondan sonra listeyi
        // guncellemek, aslinda yazilmamis gruplari "yazilmis" saymak olurdu.
        var yayinlanan = DesiredGroups(state).ToList();
        if (!yayinlanan.SequenceEqual(state.PublishedGroups, StringComparer.OrdinalIgnoreCase))
        {
            state.PublishedGroups = yayinlanan;
            await UpsertStateAsync(state, cancellationToken);
        }

        return string.Empty;
    }

    /// <summary>Panelin bu bolgede olmasini istedigi "TUR|konak" gruplari.</summary>
    private static IEnumerable<string> DesiredGroups(DnsZoneState state) => state.Records
        .Select(record => $"{record.Type.ToUpperInvariant()}|{record.Host}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 252 karakteri asan TXT kayitlarini, ana senkrondan AYRI turlarda yazar.
    /// <para>
    /// Neden ayri: uzun deger icin gereken bolge dosyasi duzenlemesi ve ZoneReload, bolgeyi
    /// kilitliyor. Ayni script icinde yapildiginda sonraki kayit islemleri sessizce dusuyor ve
    /// SPF/DMARC gibi kayitlar "zaten var, silinemedi, eklenemedi" durumunda kaliyordu.
    /// </para>
    /// <para>
    /// Hata FIRLATILMAZ: uzun kayit yazilamasa bile bolgenin geri kalani gecerlidir. Basarisizlik
    /// gunluge yazilir ve denetim zaten "yayinlanmamis kayit" olarak bunu gorur.
    /// </para>
    /// </summary>
    private async Task WriteLongTxtRecordsAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var uzunlar = state.Records
            .Where(record => string.Equals(record.Type, "TXT", StringComparison.OrdinalIgnoreCase)
                             && record.Value.Length > 252)
            .ToList();

        foreach (var record in uzunlar)
        {
            var script =
                "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
                $"$zoneName = '{EscapePowerShellLiteral(state.ZoneName)}'" + Environment.NewLine +
                BuildTxtLine(EscapePowerShellLiteral(record.Host), record.Value, Math.Clamp(record.Ttl, 60, 86400));

            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success)
            {
                // MASKELENEREK yazılır. Komut ayrıntısı normalde `AppException` kurucusundan
                // geçer ve orada maskelenir (0.8.9); BURASI günlüğe DOĞRUDAN yazan tek yerdi,
                // yani tek kaynağın dışında kalmıştı. TXT kaydının değeri kiracının yazdığı
                // serbest metindir ve başarısız komutun stderr'i o değeri geri yankılar —
                // doğrulama jetonu, API anahtarı, ne konduysa. Aynı kapıdan geçirmenin bedeli yok.
                logger.LogWarning(
                    "Uzun TXT kaydi yazilamadi ({Zone} / {Host}): {Error}",
                    state.ZoneName,
                    record.Host,
                    SecretRedactor.SanitizeDetails(BuildCommandDetails(result)));
            }
        }
    }

    /// <summary>
    /// İKİNCİL (slave) bölgeyi Windows DNS'te var eder ve master listesini uygular.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kayıt YAZILMAZ — yazılamaz da: ikincil bölge salt okunurdur, içeriği master'dan AXFR ile
    /// gelir. Bu yüzden birincil senkronun tüm sil/ekle bölümü burada bilerek YOKTUR.
    /// </para>
    /// <para>
    /// Üç durum ayrı ele alınır çünkü Windows'ta her biri FARKLI cmdlet ister ve yanlış olanı
    /// çağırmak hata verir: bölge yoksa <c>Add-DnsServerSecondaryZone</c>, birincilse
    /// <c>ConvertTo-DnsServerSecondaryZone</c>, zaten ikincilse yalnızca master listesini güncelleyen
    /// <c>Set-DnsServerSecondaryZone</c>. Tek bir cmdlet'e bahse girmek, kipi ikinci kez uygulamayı
    /// (ya da elle ikincil yapılmış bir bölgeyi) kırardı.
    /// </para>
    /// <para>
    /// Sonda tam transfer TETİKLENİR: aksi hâlde bölge SOA yenileme aralığı dolana kadar BOŞ kalır
    /// ve alan adı "ikincile geçtim" dendiği andan itibaren saatlerce çözülmez.
    /// </para>
    /// </remarks>
    internal static string BuildSecondaryZoneScript(string zoneName, IReadOnlyList<string> masterServers)
    {
        var zone = EscapePowerShellLiteral(zoneName);
        var masters = string.Join(",", masterServers.Select(ip => $"'{EscapePowerShellLiteral(ip)}'"));

        return string.Join(Environment.NewLine,
        [
            "$ErrorActionPreference = 'Stop'",
            $"$zoneName = '{zone}'",
            "if (-not (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)) { exit 21 }",
            $"$masters = @({masters})",
            "$mevcut = Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue",
            "if (-not $mevcut) {",
            "  Add-DnsServerSecondaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" -MasterServers $masters | Out-Null",
            "} elseif ($mevcut.ZoneType -ne 'Secondary') {",
            "  ConvertTo-DnsServerSecondaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" -MasterServers $masters -Force | Out-Null",
            "} else {",
            "  Set-DnsServerSecondaryZone -Name $zoneName -MasterServers $masters | Out-Null",
            "}",
            // Transfer BEST-EFFORT: master o an ulaşılamazsa kip değişikliği yine de geçerlidir,
            // bölge sonraki yenilemede dolar. Burada patlamak, doğru yapılandırmayı geri alırdı.
            "if (Get-Command Start-DnsServerZoneTransfer -ErrorAction SilentlyContinue) {",
            "  Start-DnsServerZoneTransfer -Name $zoneName -FullTransfer -ErrorAction SilentlyContinue | Out-Null",
            "}"
        ]);
    }

    /// <summary>
    /// Birincil bölgenin AXFR/NOTIFY satırı: izin verilen IP varsa yalnız onlara transfer + NOTIFY,
    /// yoksa transfer TAMAMEN KAPALI (güvenli varsayılan — açık AXFR bölgenin tamamını herkese verir).
    /// </summary>
    internal static string BuildTransferAclLine(IReadOnlyList<string> notifyTargets)
    {
        if (notifyTargets.Count == 0)
        {
            return "Set-DnsServerPrimaryZone -Name $zoneName -SecureSecondaries NoTransfer | Out-Null";
        }

        var ipList = string.Join(",", notifyTargets.Select(ip => $"'{EscapePowerShellLiteral(ip)}'"));
        return $"Set-DnsServerPrimaryZone -Name $zoneName -SecureSecondaries TransferToSecureServers -SecondaryServers {ipList} -Notify NotifyServers -NotifyServers {ipList} | Out-Null";
    }

    private static string BuildSyncScript(DnsZoneState state, IReadOnlyList<string> notifyTargets)
    {
        // Silinecek gruplar = SIMDI istenenler + ESKIDEN panelin yazdiklari.
        //
        // Ikincisi olmadan sizinti oluyordu: bir grubun son kaydi panelden silininde grup listeden
        // duser, silme komutu uretilmez ve kayit Windows DNS'te canli kalir. Bir MX ya da TXT'nin
        // boyle "hayalet" kalmasi postayi yanlis sunucuya yonlendirebilir.
        var istenen = state.Records
            .GroupBy(record => $"{record.Type.ToUpperInvariant()}|{record.Host}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Type = group.First().Type.ToUpperInvariant(), Host = group.First().Host })
            .ToList();

        var istenenAnahtarlar = istenen.Select(item => $"{item.Type}|{item.Host}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oksuzler = state.PublishedGroups
            .Where(key => !istenenAnahtarlar.Contains(key))
            .Select(key => key.Split('|', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            // SOA ve zone kokundeki NS bilerek DISARIDA: Windows bunlari bolgeyle birlikte kendisi
            // olusturur, silinmeleri bolgeyi bozar.
            // SOA ve BOLGE KOKUNDEKI NS disarida: Windows bunlari bolgeyle birlikte kendisi
            // olusturur. Kok NS'lerin silinmesi bolgeyi YETKISIZ birakir (lame delegation) ve alan
            // adi tumuyle cozulmez olur. Yorum bunu soyluyordu ama kod yalnizca SOA'yi disliyordu.
            .Where(parts => !string.Equals(parts[0], "SOA", StringComparison.OrdinalIgnoreCase))
            .Where(parts => !(string.Equals(parts[0], "NS", StringComparison.OrdinalIgnoreCase)
                              && string.Equals(parts[1], "@", StringComparison.Ordinal)))
            .Select(parts => new { Type = parts[0].ToUpperInvariant(), Host = parts[1] });

        var groups = istenen.Concat(oksuzler).ToArray();

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$zoneName = '{EscapePowerShellLiteral(state.ZoneName)}'",
            "if (-not (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)) { exit 21 }",
            "$mevcut = Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue",
            "if (-not $mevcut) {",
            "  Add-DnsServerPrimaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" | Out-Null",
            // BOLGE IKINCILSE ONCE BIRINCILE CEVRILIR.
            //
            // Neden burada: kullanici ikincil kipten birincile donunce Windows'taki bolge HALA
            // "Secondary" olur ve o bolgeye kayit yazilamaz. Cevirmeden devam etseydik senkron
            // her kayitta hata verir, panel "bolge yazilamadi" der ama SEBEBI soylemezdi. Cevrim
            // burada oldugu icin, kipten donusun ardindan panelin kayitlari kendiliginden yeniden
            // yayinlanir (Plesk de kipten donuste eski kayitlari geri getirir).
            "} elseif ($mevcut.ZoneType -eq 'Secondary') {",
            "  ConvertTo-DnsServerPrimaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" -Force | Out-Null",
            "}"
        };

        // panel "zone transfer ACL": izin verilen IP varsa yalnız onlara AXFR + NOTIFY; yoksa transfer KAPALI (güvenli + panel varsayılanı).
        // Liste bölgeye özel IP'ler ile SUNUCU-GENELİ ikincil DNS sunucularının birleşimidir.
        lines.Add(BuildTransferAclLine(notifyTargets));

        foreach (var group in groups)
        {
            lines.Add($"Get-DnsServerResourceRecord -ZoneName $zoneName -Name '{EscapePowerShellLiteral(group.Host)}' -RRType '{group.Type}' -ErrorAction SilentlyContinue | Remove-DnsServerResourceRecord -ZoneName $zoneName -Force -ErrorAction SilentlyContinue");
        }

        // TEK BOZUK KAYIT BOLGENIN TAMAMINI GOTURMEMELI.
        //
        // Script tek bir PowerShell calistirmasidir ve basinda $ErrorActionPreference='Stop' vardir.
        // Once TUM gruplar silinip sonra tek tek yeniden eklendigi icin, ortadaki bir kayit
        // patladiginda script komple duruyordu: o noktadan sonraki hicbir kayit yazilmiyor,
        // basta silinenler de silinmis kaliyordu. Canlida tam olarak bu oldu -- 255 karakteri asan
        // DKIM kaydi yuzunden bolgede _dmarc dahil hicbir sey kalmadi, ustelik panel "onarildi"
        // diyordu. Artik her ekleme kendi icinde yakalaniyor: saglam kayitlar yazilir, basarisizlar
        // toplanip SONUNDA bildirilir (cikis kodu yine sifirdan farkli olur, yani hata gizlenmez).
        lines.Add("$basarisiz = @()");

        foreach (var record in SortRecords(state.Records))
        {
            var host = EscapePowerShellLiteral(record.Host);
            var value = EscapePowerShellLiteral(record.Value);
            var ttl = Math.Clamp(record.Ttl, 60, 86400);
            var command = record.Type.ToUpperInvariant() switch
            {
                "A" => $"Add-DnsServerResourceRecordA -ZoneName $zoneName -Name '{host}' -IPv4Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "AAAA" => $"Add-DnsServerResourceRecordAAAA -ZoneName $zoneName -Name '{host}' -IPv6Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "CNAME" => $"Add-DnsServerResourceRecordCName -ZoneName $zoneName -Name '{host}' -HostNameAlias '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "MX" => $"Add-DnsServerResourceRecordMX -ZoneName $zoneName -Name '{host}' -MailExchange '{value}' -Preference {record.Priority ?? 10} -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                // UZUN TXT ANA SENKRONDA DENENMEZ.
                //
                // Uzun deger icin gereken bolge dosyasi + ZoneReload adimlari BOLGEYI KILITLIYOR
                // ve ayni script icindeki sonraki kayit islemleri (silme/ekleme) sessizce
                // dusuyor. Canlida tam olarak bu oldu: SPF ve DMARC "zaten var" oldugu icin
                // silinemiyor, sonra "Failed to create resource record" ile eklenemiyordu --
                // yani uzun TXT icin yazdigim cozum senkronun GERI KALANINI bozuyordu.
                // Uzun kayitlar ana senkron BITTIKTEN SONRA ayri bir turda yazilir.
                "TXT" => record.Value.Length <= 252 ? BuildTxtLine(host, record.Value, ttl) : string.Empty,
                "NS" => $"Add-DnsServerResourceRecord -ZoneName $zoneName -NS -Name '{host}' -NameServer '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "PTR" => $"Add-DnsServerResourceRecordPtr -ZoneName $zoneName -Name '{host}' -PtrDomainName '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "SRV" => BuildSrvLine(host, record.Value, record.Priority, ttl),
                "TLSA" => BuildTlsaLine(host, record.Value, ttl),
                // CAA: Windows DNS'te native parametre yok → RFC 3597 bilinmeyen-kayıt yolu.
                "CAA" => BuildCaaLine(host, record.Value, ttl),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            // Kayit tanimi hata mesajinda gorunsun ki hangi kaydin reddedildigi bilinsin.
            var etiket = EscapePowerShellLiteral($"{record.Type} {record.Host}");
            lines.Add("try {");
            lines.Add(command);
            // ETİKET TEK TIRNAKLI DİZEDE. Eskiden ÇİFT tırnaklıydı ve tek kaçış EscapePowerShellLiteral
            // (yalnız `'` ikiler) idi — yani `"` ve `$(...)` hiç kaçırılmıyordu. Çift tırnaklı
            // PowerShell dizesinde `$(...)` ÇALIŞIR. Normal kayıt yolunda Host, HostPattern ile
            // daraltıldığı için ulaşılamıyordu; ama ŞABLON yolu (BuildDefaultRecordsAsync →
            // ResetDefaultsAsync) NormalizeRecord'a HİÇ uğramıyor ve Host'u yalnız Trim ediyor.
            // Yani buradaki tek savunma, başka bir yoldaki regex'ti.
            lines.Add($"}} catch {{ $basarisiz += '{etiket}' + ': ' + $_.Exception.Message }}");
        }

        // BASARISIZLIK LISTESI STANDART CIKTIYA yazilir, Write-Error ILE DEGIL.
        //
        // Neden: scriptin basinda $ErrorActionPreference='Stop' var. Write-Error bu kipte
        // SONLANDIRICI hataya doner; script "exit 22"ye hic ulasamaz, PowerShell 1 ile cikar ve
        // hata akisina asil mesaj yerine hatanin gectigi SATIR BAGLAMINI (yani scriptin metnini)
        // basar. Sonuc: kullaniciya sayfalarca script gorunur, gercek sebep gorunmez. Teshis
        // mekanizmasini kendi kendine bogan bir kurulumdu.
        lines.Add("if ($basarisiz.Count -gt 0) {");
        lines.Add("  [Console]::Out.WriteLine('ADAPANEL-FAIL: ' + ($basarisiz -join ' | '))");
        lines.Add("  exit 22");
        lines.Add("}");

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    // Aşağıdaki üç üretici test edilebilir olsun diye ayrı: Windows DNS'e yazılan komut satırının
    // birebir doğru olması gerekiyor (yanlış parametre = kayıt sessizce yayınlanmaz).
    /// <summary>
    /// TXT kaydi. 255 KARAKTERI ASAN degerler icin farkli bir yol kullanilir.
    /// <para>
    /// Neden: DNS'te tek bir TXT "character-string" en fazla 255 bayttir; daha uzun degerler BIRDEN
    /// COK parcaya bolunerek yazilir (RFC 1035) ve dogrulayicilar parcalari birlestirir (DKIM icin
    /// RFC 6376 bunu acikca soyler). <c>Add-DnsServerResourceRecord -Txt</c> bu bolmeyi YAPMAZ;
    /// uzun degerde "InputObject for resource record has an invalid value" (WIN32 203) ile reddeder.
    /// </para>
    /// <para>
    /// Bu, canli sunucuda DKIM'i tamamen calismaz kiliyordu: RSA acik anahtari ~450 karakterdir,
    /// yani RSA DKIM kaydi HICBIR kurulumda yazilamiyordu. Ed25519 kaydi kisa oldugu icin geciyor
    /// ve sorun yarim gorunuyordu. Kisa degerlerde kanitlanmis cmdlet yolu KORUNUR; yalnizca uzun
    /// degerde dnscmd'e dusulur, cunku dnscmd birden cok parcayi ayri argüman olarak kabul eder.
    /// </para>
    /// </summary>
    internal static string BuildTxtLine(string host, string value, int ttl)
    {
        // 252, 255 DEGIL: CRLF ile ayrilan yontemde ayirici da parcanin icinde sayilir.
        const int MaxStringLength = 252;

        if (value.Length <= MaxStringLength)
        {
            return $"Add-DnsServerResourceRecord -ZoneName $zoneName -Txt -Name '{EscapePowerShellLiteral(host)}' " +
                   $"-DescriptiveText '{EscapePowerShellLiteral(value)}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null";
        }

        // UZUN DEGER: UC YOL SIRAYLA DENENIR VE HER DENEMEDEN SONRA KAYIT GERI OKUNUR.
        //
        // Neden boyle: 255'ten uzun bir TXT kaydini Windows DNS'e yazmanin tek dogru yolu YOK.
        // Yonetim arayuzu (MMC) bunu yapabiliyor, yani sunucunun kendisi destekliyor; ama komut
        // satiri araclari surume ve yapilandirmaya gore farkli davraniyor. Canlida sirasiyla
        // denendi ve reddedildi: cmdlet'e ayrilmamis uzun deger, dnscmd'ye ayri tirnakli parcalar
        // (ERROR_INVALID_PARAMETER), cmdlet'e CRLF ile ayrilmis deger.
        //
        // Bu yuzden panel artik TEK bir yola bahse girmiyor: uc yolu sirayla dener ve her birinden
        // sonra kaydin GERCEKTEN olustugunu okuyarak dogrular. Ilk dogrulanan kazanir. Hicbiri
        // tutmazsa uc yolun da hatasi birlikte bildirilir -- boylece bir sonraki adim tahmine
        // degil olcume dayanir.
        var chunks = new List<string>();
        for (var index = 0; index < value.Length; index += MaxStringLength)
        {
            chunks.Add(value.Substring(index, Math.Min(MaxStringLength, value.Length - index)));
        }

        var crlfBirlesik = string.Join(
            " + [char]13 + [char]10 + ",
            chunks.Select(chunk => $"'{EscapePowerShellLiteral(chunk)}'"));

        // Her parca cift tirnak icinde, aralarinda bosluk: '"p1" "p2"'
        var parcaliBirlesik = string.Join(
            " + ' ' + ",
            chunks.Select(chunk => $"[char]34 + '{EscapePowerShellLiteral(chunk)}' + [char]34"));

        var adLiteral = $"'{EscapePowerShellLiteral(host)}'";
        var tamDeger = $"'{EscapePowerShellLiteral(value)}'";
        var dnscmdDeger = "\"" + value.Replace("\"", string.Empty) + "\"";

        var satirlar = new List<string>
        {
            $"$adaAd = {adLiteral}",
            $"$adaTam = {tamDeger}",
            $"$adaCrlf = {crlfBirlesik}",
            // Bolge dosyasi soz dizimi: ardisik tirnakli parcalar. Cift tirnak PowerShell dizesine
            // [char]34 ile konuyor -- kacis dizileri bu zincirde tekrar tekrar bozuldu.
            $"$adaParcali = {parcaliBirlesik}",
            // Her yolun mesaji AYRI kirpilir: aksi halde tek bir yolun uzun ciktisi digerlerinin
            // mesajini rapordan kaydirir. Canlida tam olarak bu oldu -- ucuncu yolun (WMI) ne
            // dedigi hic gorunmedi.
            // Yerel programlarin (dnscmd) stderr'i 2>&1 ile akisa alindiginda PowerShell 5.1 her
            // satiri NativeCommandError'a sarar; scriptin basindaki $ErrorActionPreference='Stop'
            // altinda bu SONLANDIRICI hataya doner ve blok oracikta biter -- yani sonraki yollara
            // (WMI, bolge dosyasi) HIC GELINMEZ. Blok boyunca gecici olarak gevsetiliyor.
            // DOGRULAMA BIZIM DEGERIMIZI ARAR, "herhangi bir TXT var mi" diye SORMAZ.
            //
            // Ayni konakta baska bir TXT bulunmasi yeterdi (ornegin '@' konagindaki SPF): olcum
            // true doner, kalan yollar atlanir ve panel kayit yazilmamisken TAM BASARI raporlardi.
            // Windows uzun TXT'i parcalara boldugu icin parcalar birlestirilip karsilastiriliyor.
            "$adaOlcum = { [bool](Get-DnsServerResourceRecord -ZoneName $zoneName -Name $adaAd -RRType Txt -ErrorAction SilentlyContinue | " +
            "Where-Object { ($_.RecordData.DescriptiveText -join '') -eq $adaTam }) }",
            "$adaEskiEap = $ErrorActionPreference",
            "$ErrorActionPreference = 'Continue'",
            // TRY/FINALLY: asagidaki yollardan biri sonlandirici bir hata firlatirsa (ornegin
            // gecici dosya olusturulamazsa) geri yukleme satirina HIC GELINMEZ ve EAP 'Continue'
            // olarak sizardi. O durumda scriptin geri kalanindaki Add komutlarinin hatalari ne
            // firlar ne toplanir -- panel "onarildi" der, kayitlar yoktur. Tam da ayikladigimiz
            // hata sinifi, bu kez kendi duzeltmemin icinde.
            "try {",
            @"$adaKirp = { param($m) $m = ($m -replace '\s+', ' ').Trim(); if ($m.Length -gt 200) { $m.Substring(0, 200) + '...' } else { $m } }",
            "$adaHata = @()",
            "$adaVar = $false",

            // NOT: burada TEMIZLIK YAPILMAZ.
            //
            // Eskiden bu noktada "$adaAd konagindaki tum TXT kayitlarini sil" komutu vardi ve bu
            // AYNI KONAKTAKI DIGER TXT'leri de siliyordu -- ornegin '@' konaginda hem SPF hem uzun
            // bir dogrulama kaydi varsa, uzun kayit yazilirken SPF sessizce yok oluyordu. Ustelik
            // gereksizdi: BuildSyncScript zaten her "TUR|konak" grubunu bu satirlardan ONCE siliyor.

            // 1) Cmdlet + CRLF ile ayrilmis deger.
            "try {",
            $"  Add-DnsServerResourceRecord -ZoneName $zoneName -Txt -Name $adaAd -DescriptiveText $adaCrlf -TimeToLive ([TimeSpan]::FromSeconds({ttl})) -ErrorAction Stop | Out-Null",
            "} catch { $adaHata += 'cmdlet-crlf: ' + $adaKirp.Invoke($_.Exception.Message) }",
            "$adaVar = $adaOlcum.Invoke()",

            // 2) dnscmd'ye TEK metin: belgelenen imza tek <rrdata> aliyor; uzun degeri kendisi
            //    parcalayabilir. Komut toplu is dosyasindan calisir ki tirnaklama bozulmasin.
            "if (-not $adaVar) {",
            "  $adaCmdDosya = Join-Path $env:TEMP ('ada-dns-' + [guid]::NewGuid().ToString('N') + '.cmd')",
            $"  $adaKomut = '@echo off' + [char]13 + [char]10 + 'dnscmd . /RecordAdd ' + $zoneName + ' ' + $adaAd + ' TXT {EscapeSingleQuoted(dnscmdDeger)}'",
            "  Set-Content -LiteralPath $adaCmdDosya -Value $adaKomut -Encoding ASCII",
            "  $adaCikti = (& $adaCmdDosya 2>&1) -join ' '",
            "  if ($LASTEXITCODE -ne 0) { $adaHata += 'dnscmd-tek: ' + $adaKirp.Invoke($adaCikti) }",
            "  Remove-Item -LiteralPath $adaCmdDosya -Force -ErrorAction SilentlyContinue",
            "  $adaVar = $adaOlcum.Invoke()",
            "}",

            // 3) WMI: yonetim arayuzunun (MMC) kullandigi yol. Sunucu bu kaydi yazabiliyor
            //    oldugundan, komut satiri araclari reddetse bile bu yol tutabilir.
            "if (-not $adaVar) {",
            "  try {",
            @"    $adaWmi = [wmiclass]'\\.\root\MicrosoftDNS:MicrosoftDNS_TXTType'",
            "    $adaFqdn = $adaAd + '.' + $zoneName",
            $"    $adaWmi.CreateInstanceFromPropertyData($env:COMPUTERNAME, $zoneName, $adaFqdn, 1, {ttl}, $adaTam) | Out-Null",
            "  } catch { $adaHata += 'wmi: ' + $adaKirp.Invoke($_.Exception.Message) }",
            "  $adaVar = $adaOlcum.Invoke()",
            "}",

            // 4) BOLGE DOSYASINA DOGRUDAN YAZ.
            //
            // Neden gerekli: yukaridaki uc yol da AYNI duvara carpiyor -- bu sunucunun DNS API'si
            // tek bir character-string'de 255 bayti asmayi kabul etmiyor (cmdlet "invalid value",
            // dnscmd kullanim metni, WMI "Generic failure"). Bolge dosyasi ise API'nin altindadir:
            // standart bolge dosyasi soz dizimi, parantez icinde ARDISIK tirnakli parcalari
            // dogrudan destekler ve DNS servisi bolgeyi yeniden yukledi mi kaydi coklu parca olarak
            // belleğe alir. Yonetim arayuzunun (MMC) yaptigi bolme de sonucta budur.
            //
            // Yalnizca DOSYA TABANLI bolgelerde calisir; AD tumlesik bolgede dosya yoktur, o durumda
            // bu adim sessizce atlanir ve hata raporu digerlerini bildirir.
            "if (-not $adaVar) {",
            "  try {",
            "    $adaZonePath = Join-Path $env:SystemRoot ('System32' + [char]92 + 'dns' + [char]92 + $zoneName + '.dns')",
            // ONCE BELLEGI DOSYAYA YAZ.
            //
            // DNS servisi bolgeyi BELLEKTE tutar ve degisiklikleri kendi zamaninda dosyaya yazar.
            // Dosyaya once eklersek, servis bellek kopyasini uzerine yazdiginda eklenen satir
            // kaybolur -- canlida tam olarak bu oldu: ZoneReload "basarili" dedi ama kayit yoktu.
            // ZoneWriteBack, bellekteki guncel hali dosyaya dokerek bizim eklememizi guvenli kilar.
            "  $adaWb = Join-Path $env:TEMP ('ada-dns-' + [guid]::NewGuid().ToString('N') + '.cmd')",
            "  Set-Content -LiteralPath $adaWb -Value ('@echo off' + [char]13 + [char]10 + 'dnscmd . /ZoneWriteBack ' + $zoneName) -Encoding ASCII",
            "  (& $adaWb 2>&1) | Out-Null",
            "  Remove-Item -LiteralPath $adaWb -Force -ErrorAction SilentlyContinue",
            "  Start-Sleep -Milliseconds 400",
            "    if (Test-Path -LiteralPath $adaZonePath) {",
            // Bolge dosyasi son satirda yeni satirla bitmiyorsa eklenen satir ONCEKI satira
            // YAPISIR ve iki kayit da bozulur. Once dosyanin sonuna yeni satir garantilenir.
            "      $adaHam = [System.IO.File]::ReadAllText($adaZonePath)",
            "      if ($adaHam.Length -gt 0 -and -not $adaHam.EndsWith([string][char]10)) { [System.IO.File]::AppendAllText($adaZonePath, [string][char]13 + [string][char]10) }",
            $"      $adaSatir = $adaAd + ' {ttl} IN TXT ( ' + $adaParcali + ' )'",
            "      Add-Content -LiteralPath $adaZonePath -Value $adaSatir -Encoding ASCII",
            "      $adaYukle = Join-Path $env:TEMP ('ada-dns-' + [guid]::NewGuid().ToString('N') + '.cmd')",
            "      Set-Content -LiteralPath $adaYukle -Value ('@echo off' + [char]13 + [char]10 + 'dnscmd . /ZoneReload ' + $zoneName) -Encoding ASCII",
            // BOLGE KILITLIYSE (DNS_ERROR_ZONE_LOCKED 9607) TEKRAR DENE.
            //
            // Senkron scripti ayni bolgede kayit silip ekliyor; ortasinda yeniden yukleme istemek
            // kilide carpiyor. Kilit gecicidir, bu yuzden kisa araliklarla birkac kez denenir.
            "      $adaReload = ''",
            "      $adaReloadKod = -1",
            "      for ($adaDeneme = 0; $adaDeneme -lt 8; $adaDeneme++) {",
            "        $adaReload = (& $adaYukle 2>&1) -join ' '",
            "        $adaReloadKod = $LASTEXITCODE",
            "        if ($adaReloadKod -eq 0) { break }",
                    // Artan bekleme: kilit senkron scriptinin kendi islemlerinden kaynaklaniyor ve
            // sabit kisa aralik yetmiyordu (canlida 6x700ms sonrasi hala 9607).
            "        Start-Sleep -Milliseconds (400 + ($adaDeneme * 500))",
            "      }",
            "      Remove-Item -LiteralPath $adaYukle -Force -ErrorAction SilentlyContinue",
            "      Start-Sleep -Milliseconds 800",
            // SESSIZ DAL KAPATILDI: satir eklenip yeniden yukleme yapildigi halde kayit
            // gorunmuyorsa bunu SOYLEMEK gerekir. Onceki surumde bu durumda hicbir mesaj
            // uretilmiyordu ve raporda dorduncu yol hic gecmiyordu -- kullanici hakli olarak
            // \"bu yol calisti mi calismadi mi\" diye soramadi bile.
            "      $adaSonrasi = $adaOlcum.Invoke()",
            "      if (-not $adaSonrasi) { $adaHata += 'zonefile: satir eklendi, ZoneReload(' + $adaReloadKod + ') ' + $adaKirp.Invoke($adaReload) + ' -- kayit yine gorunmedi' }",
            "    } else { $adaHata += 'zonefile: bolge dosyasi bulunamadi: ' + $adaZonePath }",
            "  } catch { $adaHata += 'zonefile: ' + $adaKirp.Invoke($_.Exception.Message) }",
            "  $adaVar = $adaOlcum.Invoke()",
            "}",

            "} finally { $ErrorActionPreference = $adaEskiEap }",
            "if (-not $adaVar) { throw ($adaHata -join ' || ') }"
        };

        return string.Join(Environment.NewLine, satirlar);
    }

    /// <summary>Tek tirnakli PowerShell dizesi icin kacis: yalnizca tek tirnak ikilenir.</summary>
    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    internal static string BuildSrvLine(string host, string value, int? priority, int ttl)
    {
        var (weight, port, target) = ParseSrvValue(value);
        return $"Add-DnsServerResourceRecord -ZoneName $zoneName -Srv -Name '{host}' " +
               $"-DomainName '{EscapePowerShellLiteral(target)}' -Priority {priority ?? 0} " +
               $"-Weight {weight} -Port {port} -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null";
    }

    internal static string BuildTlsaLine(string host, string value, int ttl)
    {
        var (usage, selector, matching, data) = ParseTlsaValue(value);
        return $"Add-DnsServerResourceRecord -ZoneName $zoneName -Tlsa -Name '{host}' " +
               $"-CertificateUsage {usage} -Selector {selector} -MatchingType {matching} " +
               $"-CertificateAssociationData '{EscapePowerShellLiteral(data)}' " +
               $"-TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null";
    }

    internal static string BuildCaaLine(string host, string recordValue, int ttl)
    {
        var (flags, tag, value) = ParseCaaValue(recordValue);
        var rdata = BuildCaaRecordData(flags, tag, value);
        return $"Add-DnsServerResourceRecord -ZoneName $zoneName -Unknown -Name '{host}' " +
               $"-Type 257 -RecordData {rdata} -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null";
    }

    // İzin verilen AXFR hedef IP'lerini doğrular (IPv4/IPv6), kırpar, tekrarları atar. Geçersiz IP → 400.
    private static List<string> NormalizeZoneTransferIps(string[]? ips)
        => NormalizeIpList(ips, "dns.invalid_transfer_ip");

    /// <summary>
    /// Kullanıcıdan gelen IP listesini doğrular ve KANONİK biçimde döner; tekrarları atar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden kanonikleştiriyoruz (sadece doğrulamakla yetinmiyoruz):</b> bu değerler doğrudan
    /// PowerShell'e gömülüyor. Tırnak kaçırma tek başına savunma olarak zayıftır — bir gün bir
    /// çağrı yeri tırnaklamayı unutur ya da <c>IPAddress.TryParse</c>'ın kabul ettiği egzotik bir
    /// biçim (kapsam kimlikli IPv6, baştaki/sondaki boşluk) beklenmedik bir metin taşır. Bu yüzden
    /// script'e giden metin KULLANICININ yazdığı dizge değil, <see cref="IPAddress"/>'in kendi
    /// ürettiği metindir; ardından yalnız <c>[0-9a-fA-F:.]</c> içerdiği ayrıca doğrulanır.
    /// Böylece komut enjeksiyonu tek bir kaçırma hatasına bağlı kalmaz.
    /// </para>
    /// <para>
    /// Kapsam kimliği (<c>fe80::1%12</c>) bu yüzden REDDEDİLİR: hem yüzde işareti ve arayüz adı
    /// taşır hem de bir master/ikincil DNS sunucusu adresi olarak zaten anlamsızdır.
    /// </para>
    /// </remarks>
    internal static List<string> NormalizeIpList(string[]? ips, string errorCode)
    {
        var result = new List<string>();
        if (ips is null)
        {
            return result;
        }

        foreach (var raw in ips)
        {
            var ip = raw?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(ip))
            {
                continue;
            }

            if (!IPAddress.TryParse(ip, out var parsed))
            {
                throw new AppException(errorCode, 400, 400);
            }

            var kanonik = parsed.ToString();
            if (!kanonik.All(karakter => Uri.IsHexDigit(karakter) || karakter is '.' or ':'))
            {
                throw new AppException(errorCode, 400, 400);
            }

            if (!result.Contains(kanonik, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(kanonik);
            }
        }

        return result;
    }

    /// <summary>
    /// Bölgenin FİİLİ AXFR/NOTIFY hedefleri: bölgeye özel liste + sunucu-geneli ikincil DNS
    /// sunucuları. Sıra korunur, tekrarlar atılır.
    /// </summary>
    /// <remarks>
    /// Birleştirme ŞART: sunucu-geneli ikincil sunucu tanımlayıp bölgede ayrıca IP giren bir
    /// yönetici, iki listeden birinin diğerini ezmesini beklemez. Ezseydi ya ikincil sunucu
    /// bölgeyi çekemez (yedek DNS sessizce ölür) ya da bölgeye özel izin kaybolurdu.
    /// </remarks>
    internal static List<string> MergeNotifyTargets(IEnumerable<string>? zoneIps, IEnumerable<string>? serverWideIps)
    {
        var result = new List<string>();
        foreach (var ip in (zoneIps ?? []).Concat(serverWideIps ?? []))
        {
            if (!string.IsNullOrWhiteSpace(ip) && !result.Contains(ip, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(ip);
            }
        }

        return result;
    }

    private static DnsRecordState NormalizeRecord(UpsertDnsRecordRequest request, string zoneName)
    {
        return NormalizeRecord(new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim(),
            Type = request.Type,
            Host = request.Host,
            Value = request.Value,
            Ttl = request.Ttl,
            Priority = request.Priority
        }, zoneName);
    }

    private static DnsRecordState NormalizeRecord(DnsRecordState record, string zoneName)
    {
        var type = record.Type.Trim().ToUpperInvariant();
        if (!SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("dns.invalid_record_type", 400, 400);
        }

        var host = NormalizeHost(record.Host, zoneName);
        var value = record.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException("dns.invalid_record_value", 400, 400);
        }

        ValidateRecordValue(type, value, record.Priority);
        return new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            Type = type,
            Host = host,
            Value = NormalizeRecordValue(type, value),
            Ttl = Math.Clamp(record.Ttl <= 0 ? 3600 : record.Ttl, 60, 86400),
            Priority = type switch
            {
                "MX" => Math.Clamp(record.Priority ?? 10, 0, 65535),
                "SRV" => Math.Clamp(record.Priority ?? 0, 0, 65535),
                _ => null
            },
            IsManaged = record.IsManaged
        };
    }

    internal static void ValidateRecordValue(string type, string value, int? priority)
    {
        // DENETİM KARAKTERİ = SATIR ENJEKSİYONU. Her kayıt tipi için, tipe bakmadan önce.
        //
        // TXT'nin TEK doğrulaması uzunluktu (512). 252 karakterden uzun bir TXT değeri
        // WriteLongTxtRecordsAsync yoluna düşüyor; orada değer önce bir PowerShell TEK TIRNAKLI
        // dizesine (ki çok satırlı OLABİLİR, satır sonu literal geçer), sonra Set-Content ile bir
        // .cmd DOSYASINA yazılıyor ve o .cmd çalıştırılıyor — yani değerdeki bir satır sonu, cmd
        // dosyasına İKİNCİ BİR KOMUT SATIRI olarak düşüyordu. Panel LocalSystem koşar; uç
        // Reseller'a açıktır. Aynı değer bölge dosyası yoluna da gidiyor ve orada rastgele DNS
        // kaydı enjekte ediyordu.
        //
        // Kaçış katmanı burada YANLIŞ KATMANDI: tek tırnak kaçışı dizeden çıkmayı engelliyor ama
        // dizenin İÇERİĞİNİN sonradan bir dosyaya satır satır dökülmesini engellemiyor. Bu yüzden
        // düzeltme kaçışta değil, DEĞERİN KENDİSİNDE: hiçbir DNS kayıt değerinde denetim
        // karakteri meşru değildir.
        foreach (var karakter in value)
        {
            if (char.IsControl(karakter))
            {
                throw new AppException("dns.invalid_record_value", 400, 400);
            }
        }

        switch (type)
        {
            case "A" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetwork):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "AAAA" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetworkV6):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "CNAME":
            case "NS":
                _ = NormalizeDomainValue(value);
                break;
            case "MX":
                _ = NormalizeDomainValue(value);
                if (priority is < 0 or > 65535)
                {
                    throw new AppException("dns.invalid_priority", 400, 400);
                }
                break;
            case "TXT" when value.Length > 512:
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "PTR":
                _ = NormalizeDomainValue(value);
                break;
            case "SRV":
                _ = ParseSrvValue(value);
                if (priority is < 0 or > 65535)
                {
                    throw new AppException("dns.invalid_priority", 400, 400);
                }
                break;
            case "CAA":
                _ = ParseCaaValue(value);
                break;
            case "TLSA":
                _ = ParseTlsaValue(value);
                break;
        }
    }

    /// <summary>SRV değeri: "&lt;ağırlık&gt; &lt;port&gt; &lt;hedef&gt;" (öncelik ayrı alandadır).</summary>
    internal static (int Weight, int Port, string Target) ParseSrvValue(string value)
    {
        var parts = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var weight) || weight is < 0 or > 65535 ||
            !int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
        {
            throw new AppException("dns.invalid_srv_value", 400, 400);
        }

        return (weight, port, NormalizeDomainValue(parts[2]));
    }

    /// <summary>CAA değeri: "&lt;bayrak&gt; &lt;etiket&gt; &lt;değer&gt;" (ör. 0 issue letsencrypt.org).</summary>
    internal static (byte Flags, string Tag, string Value) ParseCaaValue(string value)
    {
        var parts = (value ?? string.Empty).Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !byte.TryParse(parts[0], out var flags) ||
            parts[1].Length is 0 or > 15 ||
            !parts[1].All(char.IsAsciiLetterOrDigit))
        {
            throw new AppException("dns.invalid_caa_value", 400, 400);
        }

        // Değer tırnaklı yazılmış olabilir (0 issue "letsencrypt.org") — tırnaklar RDATA'ya girmez.
        var caaValue = parts[2].Trim().Trim('"');
        if (caaValue.Length == 0 || caaValue.Length > 255 || caaValue.Any(character => character > 126))
        {
            throw new AppException("dns.invalid_caa_value", 400, 400);
        }

        return (flags, parts[1].ToLowerInvariant(), caaValue);
    }

    /// <summary>TLSA değeri: "&lt;kullanım&gt; &lt;seçici&gt; &lt;eşleşme&gt; &lt;sertifika-hex&gt;".</summary>
    internal static (int Usage, int Selector, int MatchingType, string Data) ParseTlsaValue(string value)
    {
        var parts = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var usage) || usage is < 0 or > 3 ||
            !int.TryParse(parts[1], out var selector) || selector is < 0 or > 1 ||
            !int.TryParse(parts[2], out var matching) || matching is < 0 or > 2 ||
            parts[3].Length is 0 or > 512 ||
            !parts[3].All(Uri.IsHexDigit))
        {
            throw new AppException("dns.invalid_tlsa_value", 400, 400);
        }

        return (usage, selector, matching, parts[3].ToLowerInvariant());
    }

    /// <summary>
    /// CAA kaydının ham RDATA'sı (RFC 8659): bayrak(1) + etiket uzunluğu(1) + etiket + değer.
    /// Windows DNS'in CAA için native parametresi olmadığından RFC 3597 "-Unknown" yolu kullanılır.
    /// </summary>
    internal static string BuildCaaRecordData(byte flags, string tag, string value)
    {
        var bytes = new List<byte>(2 + tag.Length + value.Length) { flags, (byte)tag.Length };
        bytes.AddRange(Encoding.ASCII.GetBytes(tag));
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
        return string.Join(",", bytes.Select(item => "0x" + item.ToString("X2")));
    }

    private static bool IsAddressFamily(string value, System.Net.Sockets.AddressFamily family)
    {
        return IPAddress.TryParse(value, out var address) && address.AddressFamily == family;
    }

    private static string NormalizeRecordValue(string type, string value)
    {
        return type switch
        {
            "CNAME" or "MX" or "NS" or "PTR" => NormalizeDomainValue(value),
            "SRV" => FormatSrvValue(ParseSrvValue(value)),
            "CAA" => FormatCaaValue(ParseCaaValue(value)),
            "TLSA" => FormatTlsaValue(ParseTlsaValue(value)),
            _ => value.Trim()
        };
    }

    private static string FormatSrvValue((int Weight, int Port, string Target) parsed)
        => $"{parsed.Weight} {parsed.Port} {parsed.Target}";

    private static string FormatCaaValue((byte Flags, string Tag, string Value) parsed)
        => $"{parsed.Flags} {parsed.Tag} {parsed.Value}";

    private static string FormatTlsaValue((int Usage, int Selector, int MatchingType, string Data) parsed)
        => $"{parsed.Usage} {parsed.Selector} {parsed.MatchingType} {parsed.Data}";

    private static string ResolveDefaultZoneName(SiteDto site)
    {
        var hostName = site.Bindings
            .Select(binding => binding.HostName)
            .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host) && host.Contains('.'));

        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var normalizedHost = NormalizeDomainValue(hostName);
            return normalizedHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? normalizedHost[4..]
                : normalizedHost;
        }

        return NormalizeZoneName(site.Name.Contains('.') ? site.Name : $"{site.Name}.local");
    }

    private static string NormalizeZoneName(string value)
    {
        var normalized = NormalizeDomainValue(value);
        if (!normalized.Contains('.'))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeDomainValue(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    /// <summary>Kayıt konağını zone'a göreli biçime indirger; geçersizse <c>dns.invalid_host</c> atar.</summary>
    internal static string NormalizeHost(string value, string zoneName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "@" : value.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.Equals(normalized, zoneName, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        if (normalized.EndsWith($".{zoneName}", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^(zoneName.Length + 1)];
        }

        if (normalized != "@" && !HostPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_host", 400, 400);
        }

        return normalized;
    }

    private static string[] NormalizeNameServers(string[] nameServers, string zoneName)
    {
        var normalized = nameServers
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(NormalizeDomainValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : [$"ns1.{zoneName}", $"ns2.{zoneName}"];
    }

    private static IEnumerable<DnsRecordState> SortRecords(IEnumerable<DnsRecordState> records)
    {
        return records
            // Collapse historical exact copies, including the duplicate policy rows. Preserve
            // distinct TXT case, priority, TTL and ownership; these can have different meaning.
            .DistinctBy(record => (record.Type.ToUpperInvariant(), record.Host.ToLowerInvariant(),
                record.Value, record.Priority, record.Ttl, record.IsManaged))
            .OrderBy(record => Array.IndexOf(SupportedTypes, record.Type.ToUpperInvariant()))
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Value, StringComparer.OrdinalIgnoreCase);
    }

    private Task<bool> IsWindowsDnsAvailableAsync(CancellationToken cancellationToken)
        => dnsAvailability.GetAsync(cancellationToken);

    /// <summary>
    /// Windows DNS'in bölge için bildirdiği TÜRÜ okur (Primary/Secondary/Stub…). Bölge yoksa ya da
    /// ölçülemezse boş dizge döner.
    /// </summary>
    /// <remarks>
    /// "Bölge var mı" sorusunun yerini aldı: ikincil DNS desteğiyle birlikte varlık tek başına
    /// yetmiyor. Panel bölgeyi birincil sanarken Windows'ta "Secondary" duruyorsa kayıt yazma
    /// denemelerinin HEPSİ sessizce başarısız olur; bunu ancak türü ölçerek görebiliriz.
    /// </remarks>
    private static async Task<string> ReadWindowsZoneTypeAsync(string zoneName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"$z = Get-DnsServerZone -Name '{EscapePowerShellLiteral(zoneName)}' -ErrorAction SilentlyContinue; if ($z) {{ [string]$z.ZoneType }}",
            cancellationToken);

        if (!result.Success)
        {
            return string.Empty;
        }

        return result.StandardOutput
            .Split(NewLineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class DnsZoneState
    {
        public string SiteName { get; set; } = string.Empty;

        public string ZoneName { get; set; } = string.Empty;

        public string[] NameServers { get; set; } = [];

        public List<DnsRecordState> Records { get; set; } = [];

        public List<string> ZoneTransferIps { get; set; } = [];

        /// <summary>
        /// Bolge IKINCIL (slave) kipte mi: kayitlarin kaynagi panel degil, dis master sunucudur.
        /// </summary>
        public bool IsSecondary { get; set; }

        /// <summary>
        /// Ikincil kipte bolgenin cekilecegi master DNS sunuculari. Birincil kipte de SAKLANIR:
        /// kullanici kipi kapatip actiginda master listesini yeniden yazmak zorunda kalmasin.
        /// </summary>
        public List<string> MasterServers { get; set; } = [];

        /// <summary>
        /// Panelin Windows DNS'e EN SON yazdigi "TUR|konak" gruplari.
        /// <para>
        /// Neden tutuluyor: senkron scripti yalnizca panelde HALEN duran gruplari silip yeniden
        /// yaziyordu. Kullanici bir grubun son kaydini panelden silince o grup listeden dustugu icin
        /// silme komutu hic uretilmiyor ve kayit Windows DNS'te CANLI kaliyordu -- hicbir onarma da
        /// temizlemiyordu. Bu liste sayesinde "eskiden yazdigim ama artik istemedigim" gruplar
        /// hedefli olarak temizlenir.
        /// </para>
        /// <para>
        /// Neden "panelde olmayan her seyi sil" DEGIL: yonetici DNS Manager'dan elle kayit eklemis
        /// olabilir. Panelin hic yazmadigi bir kaydi silmek, kullanicinin verisini yok etmek olurdu.
        /// </para>
        /// </summary>
        public List<string> PublishedGroups { get; set; } = [];

        public int DefaultsVersion { get; set; }
    }

    private sealed class DnsRecordState
    {
        public string Id { get; set; } = string.Empty;

        public string Type { get; set; } = "A";

        public string Host { get; set; } = "@";

        public string Value { get; set; } = string.Empty;

        public int Ttl { get; set; } = 3600;

        public int? Priority { get; set; }

        public bool IsManaged { get; set; }
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled)]
    private static partial Regex DomainPattern();

    /// <summary>
    /// Kayıt konağı (zone'a göreli ad). Etiketler ALT ÇİZGİYLE başlayabilir.
    /// <para>
    /// Alt çizgi izni şart: hizmet kayıtlarının tamamı bu biçimdedir ve panelin kendi özellikleri
    /// bunlara dayanır — DMARC (<c>_dmarc</c>, üstelik YERLEŞİK ŞABLONDA), DKIM
    /// (<c>secici._domainkey</c>), SRV (<c>_sip._tcp</c>), TLSA (<c>_25._tcp.mail</c>) ve joker SSL'in
    /// kullandığı ACME DNS-01 doğrulaması (<c>_acme-challenge</c>).
    /// </para>
    /// <para>
    /// Eski desen ilk karakterin harf/rakam olmasını şart koşuyordu; sonuç olarak bu kayıtların hiçbiri
    /// eklenemiyordu ve şablonda <c>_dmarc</c> bulunduğu için DNS ekranı zone'u okurken bile
    /// "dns.invalid_host" ile düşüyordu. Canlı testte tam olarak bu görüldü.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^(?:@|(?:\*|_?[a-z0-9](?:[a-z0-9_-]{0,61}[a-z0-9])?)(?:\.(?:_?[a-z0-9](?:[a-z0-9_-]{0,61}[a-z0-9])?))*)$", RegexOptions.Compiled)]
    private static partial Regex HostPattern();
}
