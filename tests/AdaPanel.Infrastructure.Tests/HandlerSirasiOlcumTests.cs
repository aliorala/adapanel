using System.Collections;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Infrastructure.AspNet;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 1 -- "AddAt duserse handler SONA ekleniyor" (AspNetService:379/:406, PhpService:1170).
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA.</b> Uc yerde ayni dort satir vardi: <c>try { AddAt(0, handler) } catch
/// { Add(handler) }</c>. Bir ustlerindeki YORUM tam da sona eklemenin <c>.php</c> dosyalarini
/// <c>404.3</c> yaptigini yaziyordu -- yani yorum hatayi BILIYOR, kod hatayi FALLBACK olarak
/// ISLIYORDU. Ustelik donen DTO handler SIRASINI hic tasimadigi icin panel her durumda
/// "Mixed ASP.NET and PHP handlers were enabled" diyordu.
/// </para>
/// <para>
/// <b>OLCULEMEYEN, ACIKCA.</b> Bu makinede IIS (<c>Microsoft.Web.Administration.dll</c>) YOK ve
/// surec YUKSELTILMEMIS; gercek bir IIS handler koleksiyonunun <c>AddAt(0, ...)</c>'i hangi
/// kosulda reddettigi OLCULMEDI. OLCULEN sey, panelin bu iki durumu NASIL OKUDUGUDUR:
/// yansima yardimcilarinin (<see cref="IisConfigurationAccess.InsertHandlerFirst"/>,
/// <see cref="IisConfigurationAccess.SnapshotHandlers"/>) GERCEK kodu, IIS'in yerine gecen
/// bir koleksiyon uzerinde GERCEKTEN kosturulur -- ve karar kurali o kosumun URETTIGI
/// listeden beslenir, elle yazilmis bir beklentiden degil.
/// </para>
/// </remarks>
public sealed class HandlerSirasiOlcumTests
{
    // =============================================================================
    // IIS YERINE GECEN KOLEKSIYON. Yansima yardimcilarinin aradigi UYELERI tasir:
    // CreateElement(string), Add(object), AddAt(int, object), GetAttributeValue(string),
    // SetAttributeValue(string, object) ve IEnumerable.
    // =============================================================================

    private sealed class SahteOge
    {
        private readonly Dictionary<string, object?> ozellikler = new(StringComparer.OrdinalIgnoreCase);

        public SahteOge(string name = "", string path = "")
        {
            ozellikler["name"] = name;
            ozellikler["path"] = path;
        }

        public object GetAttributeValue(string attributeName)
            => ozellikler.TryGetValue(attributeName, out var value) ? value ?? string.Empty : string.Empty;

        public void SetAttributeValue(string attributeName, object? value)
            => ozellikler[attributeName] = value;
    }

    private sealed class SahteKoleksiyon(bool addAtCalisir) : IEnumerable
    {
        private readonly List<SahteOge> ogeler = [];

        public IReadOnlyList<SahteOge> Ogeler => ogeler;

        public object CreateElement(string elementTagName) => new SahteOge();

        public void Add(object element) => ogeler.Add((SahteOge)element);

        public void AddAt(int index, object element)
        {
            if (!addAtCalisir)
            {
                // GERCEK IIS'te olculen kosul degil, ONUN YERINE GECEN kosul: 0. indeks kilitli.
                throw new InvalidOperationException("index 0 kilitli");
            }

            ogeler.Insert(index, (SahteOge)element);
        }

        public void Onceden(string name, string path) => ogeler.Add(new SahteOge(name, path));

        public IEnumerator GetEnumerator() => ogeler.GetEnumerator();
    }

    private static SahteKoleksiyon DevralinmisKoleksiyon(bool addAtCalisir)
    {
        var koleksiyon = new SahteKoleksiyon(addAtCalisir);
        // IIS'in devraldigi kanonik sira: uzantisiz URL, TRACE, OPTIONS ve en sonda HER
        // istegi yakalayan StaticFile (path="*").
        koleksiyon.Onceden("ExtensionlessUrlHandler-Integrated-4.0", "*.");
        koleksiyon.Onceden("TRACEVerbHandler", "*");
        koleksiyon.Onceden("StaticFile", "*");
        return koleksiyon;
    }

    // =============================================================================
    // 1) GERCEK KOD YOLU: InsertHandlerFirst + SnapshotHandlers + Olc, ucu birden.
    // =============================================================================

    [Fact]
    public void AddAt_CALISIRSA_handler_StaticFile_ONUNE_gecer_ve_panel_sade_yapildi_der()
    {
        var koleksiyon = DevralinmisKoleksiyon(addAtCalisir: true);
        var handler = IisConfigurationAccess.InvokeMethod(koleksiyon, "CreateElement", "add");
        IisConfigurationAccess.SetAttributeValue(handler, "name", "AdaPanel.Php.php-8.3");
        IisConfigurationAccess.SetAttributeValue(handler, "path", "*.php");

        var basaKondu = IisConfigurationAccess.InsertHandlerFirst(koleksiyon, handler);

        Assert.True(basaKondu);

        var olcum = HandlerOrderMeasurement.Olc(
            IisConfigurationAccess.SnapshotHandlers(koleksiyon),
            "AdaPanel.Php.php-8.3",
            basaKondu);

        Assert.True(olcum.Measured);
        Assert.True(olcum.HandlerFound);
        Assert.True(olcum.PrecedesCatchAll);
        Assert.Equal(0, olcum.HandlerIndex);

        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "yapildi" demeli.
        Assert.True(olcum.Ordered);
        Assert.Equal(string.Empty, olcum.NoticeKey);
        Assert.False(olcum.ShadowedByCatchAll);
        Assert.False(olcum.Unverified);
    }

    /// <summary>
    /// ASIL HATA, UCTAN UCA. <c>AddAt</c> dusunce eski kod SESSIZCE sona ekliyordu; artik ayni
    /// fallback calisiyor ama sonucu OLCULUYOR ve panel "yapildi" DEMIYOR.
    /// </summary>
    [Fact]
    public void AddAt_DUSERSE_handler_SONA_ekleniyor_ve_bu_ARTIK_404_3_olarak_RAPORLANIYOR()
    {
        var koleksiyon = DevralinmisKoleksiyon(addAtCalisir: false);
        var handler = IisConfigurationAccess.InvokeMethod(koleksiyon, "CreateElement", "add");
        IisConfigurationAccess.SetAttributeValue(handler, "name", "AdaPanel.Php.php-8.3");
        IisConfigurationAccess.SetAttributeValue(handler, "path", "*.php");

        var basaKondu = IisConfigurationAccess.InsertHandlerFirst(koleksiyon, handler);

        // Fallback korunuyor (hic yazmamaktan iyidir) AMA kismi basari kaybolmuyor.
        Assert.False(basaKondu);
        Assert.Equal(4, koleksiyon.Ogeler.Count);

        var olcum = HandlerOrderMeasurement.Olc(
            IisConfigurationAccess.SnapshotHandlers(koleksiyon),
            "AdaPanel.Php.php-8.3",
            basaKondu);

        Assert.True(olcum.Measured);
        Assert.True(olcum.HandlerFound);
        Assert.False(olcum.PrecedesCatchAll);
        Assert.True(olcum.ShadowedByCatchAll);
        Assert.False(olcum.Ordered);

        // KISMI BASARI ADIYLA: kullaniciya "hangi handler onumuzde" denebilmeli.
        Assert.Equal("TRACEVerbHandler", olcum.BlockingHandlerName);
        Assert.Equal("aspnet.handler_shadowed", olcum.NoticeKey);
    }

    [Fact]
    public void Classic_ASP_handleri_de_ayni_kuralla_olculur()
    {
        var koleksiyon = DevralinmisKoleksiyon(addAtCalisir: false);
        var handler = IisConfigurationAccess.InvokeMethod(koleksiyon, "CreateElement", "add");
        IisConfigurationAccess.SetAttributeValue(handler, "name", "AdaPanel.ClassicAsp");
        IisConfigurationAccess.SetAttributeValue(handler, "path", "*.asp");

        var basaKondu = IisConfigurationAccess.InsertHandlerFirst(koleksiyon, handler);
        var olcum = HandlerOrderMeasurement.Olc(
            IisConfigurationAccess.SnapshotHandlers(koleksiyon),
            "AdaPanel.ClassicAsp",
            basaKondu);

        Assert.True(olcum.ShadowedByCatchAll);
        Assert.Equal("aspnet.handler_shadowed", olcum.NoticeKey);
    }

    // =============================================================================
    // 2) KARAR KURALI
    // =============================================================================

    [Fact]
    public void Catch_all_HIC_YOKSA_handler_nerede_olursa_olsun_SIRALIDIR()
    {
        var handlers = new[]
        {
            new HandlerSnapshot("ExtensionlessUrlHandler", "*."),
            new HandlerSnapshot("AdaPanel.Php.php-8.3", "*.php")
        };

        var olcum = HandlerOrderMeasurement.Olc(handlers, "AdaPanel.Php.php-8.3");

        Assert.True(olcum.Ordered);
        Assert.Equal(-1, olcum.CatchAllIndex);
        Assert.Equal(string.Empty, olcum.BlockingHandlerName);
    }

    /// <summary>
    /// <c>*.</c> (uzantisiz URL) ile <c>*</c> (StaticFile) AYNI SEY DEGILDIR; ikisini karistirmak
    /// panele var olmayan bir sorunu surekli uyarttirirdi.
    /// </summary>
    [Fact]
    public void Uzantisiz_URL_handleri_catch_all_SAYILMAZ()
    {
        var handlers = new[]
        {
            new HandlerSnapshot("ExtensionlessUrlHandler", "*."),
            new HandlerSnapshot("AdaPanel.Php.php-8.3", "*.php"),
            new HandlerSnapshot("StaticFile", "*")
        };

        var olcum = HandlerOrderMeasurement.Olc(handlers, "AdaPanel.Php.php-8.3");

        Assert.True(olcum.Ordered);
        Assert.Equal(2, olcum.CatchAllIndex);
        Assert.Equal(1, olcum.HandlerIndex);
    }

    [Fact]
    public void Handler_commit_sonrasi_LISTEDE_YOKSA_bu_YOK_olarak_raporlanir()
    {
        var handlers = new[] { new HandlerSnapshot("StaticFile", "*") };

        var olcum = HandlerOrderMeasurement.Olc(handlers, "AdaPanel.Php.php-8.3");

        Assert.True(olcum.Measured);
        Assert.True(olcum.Missing);
        Assert.False(olcum.Ordered);
        Assert.False(olcum.ShadowedByCatchAll);
        Assert.Equal("aspnet.handler_missing", olcum.NoticeKey);
    }

    /// <summary>
    /// BU DEPODA OLCULMUS KURAL: sorgu HATASI ile BOS SONUC ayni goruntuye DUSMEMELI.
    /// Okuma yapilamadiysa sonuc "sirasiz" DEGIL, BILINMIYOR'dur.
    /// </summary>
    [Fact]
    public void Geri_okuma_YAPILAMADIYSA_sonuc_SIRASIZ_degil_OLCULEMEDI_dir()
    {
        var olculemedi = HandlerOrderMeasurement.Olc(null, "AdaPanel.Php.php-8.3");

        Assert.True(olculemedi.Unverified);
        Assert.False(olculemedi.Measured);
        Assert.False(olculemedi.Ordered);
        Assert.False(olculemedi.ShadowedByCatchAll);
        Assert.False(olculemedi.Missing);
        Assert.Equal("aspnet.handler_order_unverified", olculemedi.NoticeKey);

        // Ayni sey handler adi bilinmiyorken de gecerli.
        Assert.True(HandlerOrderMeasurement.Olc([new HandlerSnapshot("StaticFile", "*")], string.Empty).Unverified);
    }

    [Fact]
    public void DTO_varsayilani_SIRALI_degil_OLCULMEDI_dir()
    {
        // Hicbir olcum yapilmadan uretilen bir DTO "sirali" varsaymamali.
        var dto = new AspSiteConfigurationDto { SiteName = "ornek", HasPhpHandler = true };

        Assert.False(dto.PhpHandlerOrder.Ordered);
        Assert.True(dto.PhpHandlerOrder.Unverified);
        Assert.Equal("aspnet.handler_order_unverified", dto.HandlerOrderNoticeKey);

        var atama = new SitePhpAssignmentDto { SiteName = "ornek" };
        Assert.True(atama.HandlerOrder.Unverified);
        Assert.Equal("aspnet.handler_order_unverified", atama.HandlerOrderNoticeKey);
    }

    // =============================================================================
    // 3) DTO: ILGISIZ OLCUM UYARI URETMEZ (fazla kapatma nobetcisi)
    // =============================================================================

    [Fact]
    public void PHP_handleri_YOKSA_PHP_sirasi_kullaniciyi_UYARMAZ()
    {
        var dto = new AspSiteConfigurationDto
        {
            SiteName = "ornek",
            HasPhpHandler = false,
            ClassicAspEnabled = false
        };

        // Ikisi de ilgisiz -> panel sade "yapildi" demeli, uydurma bir sorun uretmemeli.
        Assert.Equal(string.Empty, dto.HandlerOrderNoticeKey);
    }

    [Fact]
    public void Her_sey_OLCULDU_ve_YOLUNDAYSA_panel_HALA_sade_yapildi_der()
    {
        var handlers = new[]
        {
            new HandlerSnapshot("AdaPanel.Php.php-8.3", "*.php"),
            new HandlerSnapshot("AdaPanel.ClassicAsp", "*.asp"),
            new HandlerSnapshot("StaticFile", "*")
        };

        var dto = new AspSiteConfigurationDto
        {
            SiteName = "ornek",
            HasPhpHandler = true,
            PhpHandlerName = "AdaPanel.Php.php-8.3",
            ClassicAspEnabled = true
        }.WithHandlerOrder(
            HandlerOrderMeasurement.Olc(handlers, "AdaPanel.Php.php-8.3", insertedAtFront: true),
            HandlerOrderMeasurement.Olc(handlers, "AdaPanel.ClassicAsp", insertedAtFront: true));

        Assert.Equal(string.Empty, dto.HandlerOrderNoticeKey);
        Assert.True(dto.PhpHandlerOrder.Ordered);
        Assert.True(dto.ClassicAspHandlerOrder.Ordered);
    }

    [Fact]
    public void WithHandlerOrder_diger_alanlari_KAYBETMEZ()
    {
        var dto = new AspSiteConfigurationDto
        {
            SiteName = "ornek",
            ApplicationPoolName = "havuz",
            ClassicAspEnabled = true,
            ManagedRuntimeVersion = "v4.0",
            ManagedPipelineMode = "Integrated",
            Enable32BitAppOnWin64 = true,
            HasPhpHandler = true,
            PhpComponentId = "php-8.3",
            PhpHandlerName = "AdaPanel.Php.php-8.3",
            IsMixedHandlersEnabled = true
        }.WithHandlerOrder(HandlerOrderMeasurement.Olculemedi());

        Assert.Equal("ornek", dto.SiteName);
        Assert.Equal("havuz", dto.ApplicationPoolName);
        Assert.True(dto.ClassicAspEnabled);
        Assert.Equal("v4.0", dto.ManagedRuntimeVersion);
        Assert.Equal("Integrated", dto.ManagedPipelineMode);
        Assert.True(dto.Enable32BitAppOnWin64);
        Assert.True(dto.HasPhpHandler);
        Assert.Equal("php-8.3", dto.PhpComponentId);
        Assert.Equal("AdaPanel.Php.php-8.3", dto.PhpHandlerName);
        Assert.True(dto.IsMixedHandlersEnabled);
    }
}
