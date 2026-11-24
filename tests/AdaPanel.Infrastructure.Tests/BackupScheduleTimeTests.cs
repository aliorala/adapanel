using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Subscriptions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Otomatik yedeğin NE ZAMAN çalışacağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu testler gerçek bir arızadan doğdu.</b> Yedek yalnızca ARALIK biliyordu
/// (<c>son çalışma + N saat</c>). Kullanıcı 17 Ağustos 23:50'de bir kez elle "Şimdi yedekle"
/// dedi ve gecelik yedeğin saati o ana KİLİTLENDİ: sonraki tur 18 Ağustos 23:50'ye düştü.
/// Panelde yedek SAATİ seçme ayarı hiç yoktu; her yeniden başlatma, her gecikme ve elle alınan
/// her yedek saati biraz daha ileri kaydırıyordu — yedek er ya da geç trafiğin en yoğun olduğu
/// saate oturuyordu.
/// </para>
/// <para>
/// Karar <see cref="BackupSchedule"/> içinde SAF olarak durur; buradaki testler on dokuz
/// bağımlılıklı otomasyon servisini hiç kurmadan bütün sınır durumlarını ölçer.
/// </para>
/// </remarks>
public sealed class BackupScheduleTimeTests
{
    [Fact]
    public void Adjacent_daily_times_are_not_skipped_between_minute_polls()
    {
        var plan = BackupSchedule.Normalize("daily", 24, 3, 0, 1, 1, 3, 1);
        var now = Utc(2026, 9, 26, 0).AddSeconds(45);
        Assert.True(BackupSchedule.ZamaniGeldi(plan, now.AddDays(-1), now, Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, now, now.AddMinutes(1), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, now.AddMinutes(1), now.AddMinutes(2), Turkiye));
    }

    /// <summary>Türkiye: sabit UTC+03, yaz saati YOK (2016'dan beri).</summary>
    private static readonly TimeZoneInfo Turkiye =
        TimeZoneInfo.CreateCustomTimeZone("Test TR", TimeSpan.FromHours(3), "Test TR", "Test TR");

    /// <summary>
    /// Yaz saati uygulayan bir dilim (Avrupa kuralı): temel UTC+01, Mart'ın son Pazarı 02:00'de
    /// bir saat ileri, Ekim'in son Pazarı 03:00'te bir saat geri.
    /// </summary>
    /// <remarks>
    /// İşletim sisteminin saat dilimi veritabanına GÜVENİLMEZ: kural burada elle kurulur, böylece
    /// test hangi makinede koşarsa koşsun aynı anları ölçer.
    /// </remarks>
    private static readonly TimeZoneInfo YazSaatli = YazSaatliDilimKur();

    private static TimeZoneInfo YazSaatliDilimKur()
    {
        var basla = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var bit = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var kural = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            basla,
            bit);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Test AB",
            TimeSpan.FromHours(1),
            "Test AB",
            "Test AB Standart",
            "Test AB Yaz",
            [kural]);
    }

    private static BackupSchedulePlan Gunluk(int saat, int dakika = 0) =>
        BackupSchedule.Normalize("daily", 24, saat, dakika, 1, 1);

    private static DateTime Utc(int yil, int ay, int gun, int saat, int dakika = 0) =>
        new(yil, ay, gun, saat, dakika, 0, DateTimeKind.Utc);

    /// <summary>
    /// Saati dakika dakika ilerletip GERÇEKTEN kaç kez çalıştığını sayar.
    /// </summary>
    /// <remarks>
    /// Tek bir anı sorgulamak "iki kez çalışır mı" ya da "kayar mı" sorusunu cevaplamaz. Bu
    /// yüzden bütün kritik testler simülasyonla ölçülür: karar fonksiyonu gerçekte nasıl
    /// kullanılıyorsa (her dakika bir kez) öyle çağrılır.
    /// </remarks>
    private static List<DateTime> Simule(
        BackupSchedulePlan plan,
        TimeZoneInfo dilim,
        DateTime baslaUtc,
        DateTime bitUtc,
        DateTime? sonCalisma = null)
    {
        var calismalar = new List<DateTime>();
        var son = sonCalisma;
        for (var an = baslaUtc; an <= bitUtc; an = an.AddMinutes(1))
        {
            if (!BackupSchedule.ZamaniGeldi(plan, son, an, dilim))
            {
                continue;
            }

            calismalar.Add(an);
            son = an;
        }

        return calismalar;
    }

    // ---------------------------------------------------------------------------------------
    // 1) SEÇİLEN SAATTE ÇALIŞIR
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Gunluk_program_SECILEN_saatte_calisir_bir_dakika_once_calismaz()
    {
        var plan = Gunluk(3);
        // Türkiye UTC+03 → yerel 03:00 = UTC 00:00.
        var dun = Utc(2026, 8, 17, 0, 0);

        Assert.False(BackupSchedule.ZamaniGeldi(plan, dun, Utc(2026, 8, 17, 23, 59), Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, dun, Utc(2026, 8, 18, 0, 0), Turkiye));
    }

    [Fact]
    public void Gunluk_program_her_gun_AYNI_saatte_calisir_kaymaz()
    {
        // Aralık modunun asıl zararı buydu: her tur bir sonrakini kendi bitiş anından sayıyordu.
        var calismalar = Simule(
            Gunluk(3),
            Turkiye,
            Utc(2026, 8, 17, 0, 0),
            Utc(2026, 8, 22, 0, 0),
            sonCalisma: Utc(2026, 8, 16, 0, 0));

        Assert.Equal(6, calismalar.Count);
        Assert.All(calismalar, an => Assert.Equal(new TimeSpan(0, 0, 0), an.TimeOfDay));
    }

    // ---------------------------------------------------------------------------------------
    // 2) GÜN İÇİNDE İKİ KEZ ÇALIŞMAZ
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Ayni_gun_icinde_IKI_KEZ_calismaz()
    {
        var calismalar = Simule(
            Gunluk(3),
            Turkiye,
            Utc(2026, 8, 18, 0, 0),
            Utc(2026, 8, 18, 23, 59),
            sonCalisma: Utc(2026, 8, 17, 0, 0));

        Assert.Single(calismalar);
    }

    [Fact]
    public void ELLE_alinan_yedek_o_gunun_turunu_KARSILAR_iki_kez_yedeklenmez()
    {
        // Yönetici 02:00'de elle yedek aldıysa bir saat sonra otomatik tur da koşmamalı: aynı
        // veriyi iki kez yedeklemek yer ve zaman israfıdır.
        var elle = Utc(2026, 8, 18, 0, 30); // yerel 03:30, o günün 03:00 turundan SONRA
        var calismalar = Simule(
            Gunluk(3),
            Turkiye,
            Utc(2026, 8, 18, 0, 30),
            Utc(2026, 8, 18, 23, 59),
            sonCalisma: elle);

        Assert.Empty(calismalar);
    }

    // ---------------------------------------------------------------------------------------
    // 3) BİLDİRİLEN ARIZA: elle tetikleme SAATİ KAYDIRMAZ
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Elle_yedek_23_50_de_alinsa_bile_sonraki_tur_ERTESI_GUN_03_00_te_kalir()
    {
        // Kullanıcının canlı sunucusunda olan tam olarak buydu: 17 Ağustos 23:50'de bir kez elle
        // "Şimdi yedekle" dendi ve gecelik yedek 18 Ağustos 23:50'ye kilitlendi.
        var elle = Utc(2026, 8, 17, 20, 50); // yerel 17 Ağustos 23:50
        var calismalar = Simule(
            Gunluk(3),
            Turkiye,
            Utc(2026, 8, 17, 20, 50),
            Utc(2026, 8, 18, 23, 59),
            sonCalisma: elle);

        Assert.Single(calismalar);
        Assert.Equal(Utc(2026, 8, 18, 0, 0), calismalar[0]); // yerel 18 Ağustos 03:00
    }

    // ---------------------------------------------------------------------------------------
    // 4) KAÇIRILAN TUR TELAFİ EDİLİR / ÇOK GEÇ KALINMIŞSA ATLANIR
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Sunucu_03_00_te_kapaliysa_ACILISTA_hemen_yedek_alinir()
    {
        // Sunucu yerel 07:00'de açıldı: 03:00 turu kaçtı ama ertesi günü beklemek bir günü
        // tamamen yedeksiz bırakırdı.
        var plan = Gunluk(3);
        var oncekiGun = Utc(2026, 8, 17, 0, 0);

        Assert.True(BackupSchedule.ZamaniGeldi(plan, oncekiGun, Utc(2026, 8, 18, 4, 0), Turkiye));
    }

    [Fact]
    public void Telafi_penceresinin_TAM_SINIRINDA_hala_calisir()
    {
        // Günlük programda pencere 12 saat: yerel 15:00'e kadar telafi edilir.
        var plan = Gunluk(3);
        var oncekiGun = Utc(2026, 8, 17, 0, 0);

        Assert.True(BackupSchedule.ZamaniGeldi(plan, oncekiGun, Utc(2026, 8, 18, 12, 0), Turkiye));
    }

    [Fact]
    public void COK_GEC_kalinmis_tur_ATLANIR_bir_sonraki_ana_birakilir()
    {
        // Yerel 16:00'da açılan sunucuda 03:00 turunu koşturmak, dört saat sonraki gerçek turla
        // neredeyse üst üste binerdi. Atlamanın bedeli sınırlıdır: bir sonraki tur en fazla
        // yarım dönem uzaktadır.
        var plan = Gunluk(3);
        var oncekiGun = Utc(2026, 8, 17, 0, 0);

        Assert.False(BackupSchedule.ZamaniGeldi(plan, oncekiGun, Utc(2026, 8, 18, 12, 1), Turkiye));
    }

    [Fact]
    public void Bir_ay_kapali_kalan_sunucu_acilista_TEK_tur_koşar_otuz_tur_degil()
    {
        // Kaçırılan turlar birikip kuyruğa girmez: telafi "en son kaçırılan an" içindir.
        var calismalar = Simule(
            Gunluk(3),
            Turkiye,
            Utc(2026, 9, 17, 4, 0),
            Utc(2026, 9, 17, 23, 59),
            sonCalisma: Utc(2026, 8, 18, 0, 0));

        Assert.Single(calismalar);
    }

    [Fact]
    public void Acilis_telafisi_bir_kez_calisir_her_yeniden_baslatmada_TEKRARLAMAZ()
    {
        // Telafi turu koştuğu anda "son çalışma" damgası kaçırılan anın ilerisine geçer; panel
        // arka arkaya beş kez yeniden başlasa bile ikinci bir yedek alınmaz.
        var plan = Gunluk(3);
        var telafi = Utc(2026, 8, 18, 4, 0);

        Assert.False(BackupSchedule.ZamaniGeldi(plan, telafi, Utc(2026, 8, 18, 4, 1), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, telafi, Utc(2026, 8, 18, 10, 0), Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, telafi, Utc(2026, 8, 19, 0, 0), Turkiye));
    }

    // ---------------------------------------------------------------------------------------
    // 5) ESKİ KURULUMLAR BOZULMAZ
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Mod_YOKSA_eski_ARALIK_davranisi_aynen_surer()
    {
        // Eski automation.json dosyalarında mod alanı hiç yok. Yükseltmeden sonra hiçbir şey
        // değişmemeli: tur hâlâ "son çalışmadan 24 saat sonra".
        var plan = BackupSchedule.Normalize(null, 24, 3, 0, 1, 1);
        var son = Utc(2026, 8, 17, 20, 50);

        Assert.Equal(BackupSchedule.Interval, plan.Mode);
        Assert.False(BackupSchedule.ZamaniGeldi(plan, son, Utc(2026, 8, 18, 0, 0), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, son, Utc(2026, 8, 18, 20, 49), Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, son, Utc(2026, 8, 18, 20, 50), Turkiye));
    }

    [Fact]
    public void Aralik_modunda_HIC_calismamis_program_HEMEN_calisir()
    {
        // Eski davranışın ikinci yarısı: son çalışma yoksa ilk turda hemen alınırdı.
        var plan = BackupSchedule.Normalize("interval", 6, 3, 0, 1, 1);

        Assert.True(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 8, 18, 13, 0), Turkiye));
    }

    [Fact]
    public void Taninmayan_mod_ARALIGA_duser_uydurma_takvim_kurulmaz()
    {
        var plan = BackupSchedule.Normalize("yearly", 24, 3, 0, 1, 1);

        Assert.Equal(BackupSchedule.Interval, plan.Mode);
        Assert.False(plan.Takvimli);
    }

    // ---------------------------------------------------------------------------------------
    // 6) YAZ SAATİ (DST)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Ilkbahar_gecisinde_VAR_OLMAYAN_saat_istisna_firlatmaz_ve_tur_atlanmaz()
    {
        // 29 Mart 2026: saat 02:00'den 03:00'e atlar, yerel 02:30 O GÜN HİÇ YAŞANMAZ.
        // Önlem alınmasaydı ConvertTimeToUtc istisna fırlatır ve o günün otomasyon turu düşerdi.
        var plan = Gunluk(2, 30);
        var calismalar = Simule(
            plan,
            YazSaatli,
            Utc(2026, 3, 28, 0, 0),
            Utc(2026, 3, 30, 23, 59),
            sonCalisma: Utc(2026, 3, 27, 1, 30));

        // 28 Mart, 29 Mart (kaydırılmış), 30 Mart → üç tur; hiçbiri kaybolmadı.
        Assert.Equal(3, calismalar.Count);
    }

    [Fact]
    public void Sonbahar_gecisinde_ayni_duvar_saati_iki_kez_yasansa_da_yedek_BIR_KEZ_alinir()
    {
        // 25 Ekim 2026: saat 03:00'ten 02:00'ye döner, yerel 02:30 İKİ KEZ yaşanır. Karar
        // deterministik olmasaydı aynı gece iki yedek alınırdı.
        var calismalar = Simule(
            Gunluk(2, 30),
            YazSaatli,
            Utc(2026, 10, 25, 0, 0),
            Utc(2026, 10, 25, 23, 59),
            sonCalisma: Utc(2026, 10, 24, 0, 30));

        Assert.Single(calismalar);
    }

    [Fact]
    public void Yaz_saati_uygulayan_dilimde_gunluk_program_yil_boyu_gunde_TAM_BIR_KEZ_calisir()
    {
        // İki geçişi de kapsayan tek ölçüm: 20 Mart - 5 Kasım arası her gün bir tur, ne eksik ne
        // fazla. Tekrar ya da atlama olsaydı sayı tutmazdı.
        var basla = Utc(2026, 3, 20, 0, 0);
        var bit = Utc(2026, 11, 5, 0, 0);
        var calismalar = Simule(Gunluk(2, 30), YazSaatli, basla, bit, sonCalisma: Utc(2026, 3, 19, 1, 30));

        // Pencere UTC 00:00'da başlayıp bitiyor; yerel 02:30 turu 5 Kasım'da henüz gelmediği için
        // sayım 20 Mart - 4 Kasım aralığını kapsar: 230 gün, 230 tur.
        Assert.Equal(230, calismalar.Count);
        Assert.Equal(230, calismalar.Select(an => TimeZoneInfo.ConvertTimeFromUtc(an, YazSaatli).Date).Distinct().Count());
    }

    // ---------------------------------------------------------------------------------------
    // 7) HAFTALIK / AYLIK
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Haftalik_program_YALNIZCA_secilen_gunde_calisir()
    {
        // 0 = Pazar … 6 = Cumartesi. 3 = Çarşamba.
        var plan = BackupSchedule.Normalize("weekly", 24, 3, 0, 3, 1);
        var calismalar = Simule(
            plan,
            Turkiye,
            Utc(2026, 8, 17, 0, 0),
            Utc(2026, 9, 14, 0, 0),
            sonCalisma: Utc(2026, 8, 16, 0, 0));

        Assert.All(
            calismalar,
            an => Assert.Equal(DayOfWeek.Wednesday, TimeZoneInfo.ConvertTimeFromUtc(an, Turkiye).DayOfWeek));
        Assert.Equal(4, calismalar.Count);
    }

    [Fact]
    public void Aylik_program_ayin_31_i_secilse_bile_KISA_aylarda_calisir()
    {
        // "Her ayın 31'i" seçen kullanıcı yılın yarısında sessizce yedeksiz kalırdı.
        var plan = BackupSchedule.Normalize("monthly", 24, 3, 0, 1, 31);
        var calismalar = Simule(
            plan,
            Turkiye,
            Utc(2027, 2, 1, 0, 0),
            Utc(2027, 3, 1, 0, 0),
            sonCalisma: Utc(2027, 1, 31, 0, 0));

        Assert.Single(calismalar);
        var yerel = TimeZoneInfo.ConvertTimeFromUtc(calismalar[0], Turkiye);
        Assert.Equal(28, yerel.Day); // Şubat'ın SON günü
        Assert.Equal(2, yerel.Month);
    }

    [Fact]
    public void Aylik_program_ayda_TEK_KEZ_calisir()
    {
        var plan = BackupSchedule.Normalize("monthly", 24, 3, 0, 1, 15);
        var calismalar = Simule(
            plan,
            Turkiye,
            Utc(2026, 8, 1, 0, 0),
            Utc(2026, 11, 1, 0, 0),
            sonCalisma: Utc(2026, 7, 15, 0, 0));

        Assert.Equal(3, calismalar.Count);
        Assert.All(calismalar, an => Assert.Equal(15, TimeZoneInfo.ConvertTimeFromUtc(an, Turkiye).Day));
    }

    // ---------------------------------------------------------------------------------------
    // 8) EKRANDA GÖSTERİLEN "SONRAKİ ÇALIŞMA"
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Sonraki_calisma_takvimden_okunur_son_calismadan_DEGIL()
    {
        // Ekran eskiden "son çalışma + 24 saat" diyordu; elle yedek alındığında yanlış saat
        // gösteriyordu ve kullanıcı panelin yalan söylediğini ancak ertesi gün fark ediyordu.
        var sonraki = BackupSchedule.SonrakiZaman(
            Gunluk(3),
            Utc(2026, 8, 17, 20, 50),
            Utc(2026, 8, 17, 21, 0),
            Turkiye);

        Assert.Equal(Utc(2026, 8, 18, 0, 0), sonraki);
    }

    [Fact]
    public void Kacirilmis_ama_hala_telafi_edilebilir_tur_SONRAKI_CALISMA_olarak_SIMDI_gosterilir()
    {
        var simdi = Utc(2026, 8, 18, 4, 0);
        var sonraki = BackupSchedule.SonrakiZaman(Gunluk(3), Utc(2026, 8, 17, 0, 0), simdi, Turkiye);

        Assert.Equal(simdi, sonraki);
    }

    [Fact]
    public void Aralik_modunda_sonraki_calisma_ESKI_hesapla_ayni_kalir()
    {
        var plan = BackupSchedule.Normalize("interval", 6, 3, 0, 1, 1);
        var son = Utc(2026, 8, 18, 10, 0);

        Assert.Equal(Utc(2026, 8, 18, 16, 0), BackupSchedule.SonrakiZaman(plan, son, Utc(2026, 8, 18, 11, 0), Turkiye));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-7)]
    [InlineData(-6)]
    public void Due_interval_schedule_displays_now_instead_of_future_or_past(int? hoursSinceNow)
    {
        var now = Utc(2026, 10, 20, 12);
        DateTime? last = hoursSinceNow.HasValue ? now.AddHours(hoursSinceNow.Value) : null;
        var plan = BackupSchedule.Normalize("interval", 6, 3, 0, 1, 1);
        Assert.True(BackupSchedule.ZamaniGeldi(plan, last, now, Turkiye));
        Assert.Equal(now, BackupSchedule.SonrakiZaman(plan, last, now, Turkiye));
    }

    // ---------------------------------------------------------------------------------------
    // 9) NORMALİZASYON SINIRLARI
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(24, 23)]
    [InlineData(99, 23)]
    public void Saat_gecerli_araliga_cekilir(int girdi, int beklenen)
    {
        Assert.Equal(beklenen, BackupSchedule.Normalize("daily", 24, girdi, 0, 1, 1).Hour);
    }

    [Fact]
    public void Gun_ve_dakika_gecerli_araliga_cekilir()
    {
        var plan = BackupSchedule.Normalize("weekly", 24, 3, 999, 42, 0);

        Assert.Equal(59, plan.Minute);
        Assert.Equal(6, plan.DayOfWeek);
        Assert.Equal(1, plan.DayOfMonth);
    }

    [Fact]
    public void Telafi_penceresi_donemin_YARISIDIR()
    {
        Assert.Equal(TimeSpan.FromHours(12), Gunluk(3).TelafiPenceresi);
        Assert.Equal(TimeSpan.FromHours(84), BackupSchedule.Normalize("weekly", 24, 3, 0, 1, 1).TelafiPenceresi);
        Assert.Equal(TimeSpan.FromDays(15), BackupSchedule.Normalize("monthly", 24, 3, 0, 1, 1).TelafiPenceresi);
    }
    [Theory]
    [InlineData(3, 0, 15, 0)]
    [InlineData(20, 45, 6, 15)]
    [InlineData(0, 0, 23, 59)]
    [InlineData(3, 0, 3, 1)]
    public void Two_daily_times_run_once_each_in_any_order(int hour, int minute, int secondHour, int secondMinute)
    {
        var plan = BackupSchedule.Normalize("daily", 24, hour, minute, 1, 1, secondHour, secondMinute);
        var start = Utc(2026, 9, 25, 21); // local midnight
        var runs = Simule(plan, Turkiye, start, start.AddDays(3).AddMinutes(-1), start.AddMinutes(-1));
        Assert.Equal(6, runs.Count);
        var expected = new[] { new TimeSpan(hour, minute, 0), new TimeSpan(secondHour, secondMinute, 0) }.Order().ToArray();
        foreach (var day in runs.GroupBy(x => TimeZoneInfo.ConvertTimeFromUtc(x, Turkiye).Date))
            Assert.Equal(expected, day.Select(x => TimeZoneInfo.ConvertTimeFromUtc(x, Turkiye).TimeOfDay).ToArray());
    }

    [Fact]
    public void Next_run_uses_second_time_then_next_day_first_time()
    {
        var plan = BackupSchedule.Normalize("daily", 24, 3, 0, 1, 1, 15, 0);
        Assert.Equal(Utc(2026, 9, 26, 12), BackupSchedule.SonrakiZaman(plan, Utc(2026, 9, 26, 0, 8), Utc(2026, 9, 26, 0, 8), Turkiye));
        Assert.Equal(Utc(2026, 9, 27, 0), BackupSchedule.SonrakiZaman(plan, Utc(2026, 9, 26, 12, 8), Utc(2026, 9, 26, 12, 8), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, Utc(2026, 9, 26, 12, 8), Utc(2026, 9, 26, 12, 9), Turkiye));
    }

    [Fact]
    public void Catch_up_window_follows_unequal_gap_between_times()
    {
        var plan = BackupSchedule.Normalize("daily", 24, 3, 0, 1, 1, 5, 0);
        Assert.True(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 9, 26, 0, 30), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 9, 26, 1, 59), Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 9, 26, 2), Turkiye));
        Assert.True(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 9, 26, 12), Turkiye));
        Assert.False(BackupSchedule.ZamaniGeldi(plan, null, Utc(2026, 9, 26, 23, 59), Turkiye));
    }

    [Fact]
    public void Identical_times_and_disabled_second_time_both_run_once()
    {
        foreach (var second in new int?[] { null, 3 })
        {
            var plan = BackupSchedule.Normalize("daily", 24, 3, 0, 1, 1, second, 0);
            Assert.False(plan.HasSecondTime);
            Assert.Single(Simule(plan, Turkiye, Utc(2026, 9, 26, 0), Utc(2026, 9, 26, 23, 59)));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Second_daily_time_respects_subscription_quota(int quota, bool throttled)
    {
        var result = SubscriptionBackupScheduleService.KotayaCek("daily", 24, 3, 0, 1, 1, quota, 15, 30);
        Assert.Equal(throttled, result.Throttled);
        Assert.Equal(!throttled, result.Plan.HasSecondTime);
        Assert.Equal(3, result.Plan.Hour);
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("weekly")]
    [InlineData("monthly")]
    public void Second_time_is_ignored_outside_daily_mode(string mode)
    {
        var without = BackupSchedule.Normalize(mode, 24, 3, 0, 1, 1);
        var with = BackupSchedule.Normalize(mode, 24, 3, 0, 1, 1, 15, 0);
        Assert.False(with.HasSecondTime);
        Assert.Equal(Simule(without, Turkiye, Utc(2026, 9, 26, 0), Utc(2026, 9, 29, 0)),
                     Simule(with, Turkiye, Utc(2026, 9, 26, 0), Utc(2026, 9, 29, 0)));
    }

    [Theory]
    [InlineData(3, 29)]
    [InlineData(10, 25)]
    public void Two_times_respect_daylight_saving_without_duplicate_runs(int month, int day)
    {
        var plan = BackupSchedule.Normalize("daily", 24, 2, 30, 1, 1, 15, 0);
        var start = Utc(2026, month, day, 0);
        var runs = Simule(plan, YazSaatli, start, start.AddHours(21), start.AddMinutes(-1));
        Assert.Equal(2, runs.Count);
        Assert.Equal(new TimeSpan(15, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(runs[1], YazSaatli).TimeOfDay);
    }

    [Fact]
    public void Spring_shifted_times_are_sorted_by_actual_instant_and_collisions_run_once()
    {
        var start = Utc(2026, 3, 29, 0);
        var plan = BackupSchedule.Normalize("daily", 24, 2, 45, 1, 1, 3, 15);
        Assert.Equal(new[] { start.AddMinutes(75), start.AddMinutes(105) },
            Simule(plan, YazSaatli, start, start.AddHours(6), start.AddMinutes(-1)));
        plan = plan with { SecondMinute = 45 };
        Assert.Single(Simule(plan, YazSaatli, start, start.AddHours(6), start.AddMinutes(-1)));
    }

}

/// <summary>
/// Abonelik yedek programı da AYNI saat kavramını kullanır; plan kotası ile ilişkisi burada ölçülür.
/// </summary>
public sealed class SubscriptionBackupScheduleQuotaTests
{
    [Fact]
    public void Takvim_modu_kotaya_TAKILMAZ_cunku_gunde_en_fazla_bir_tur_uretir()
    {
        // Günlük/haftalık/aylık programın hepsi en çok günde bir tur demektir; "günde en çok N
        // yedek" sınırı N ≥ 1 için zaten sağlanır. Kotayı takvim moduna uygulamak, kullanıcının
        // seçtiği saati sessizce ezerdi.
        var sonuc = SubscriptionBackupScheduleService.KotayaCek(
            "daily", intervalHours: 1, hour: 3, minute: 0, dayOfWeek: 1, dayOfMonth: 1, maxBackupsPerDay: 1);

        Assert.False(sonuc.Throttled);
        Assert.Equal("daily", sonuc.Plan.Mode);
        Assert.Equal(3, sonuc.Plan.Hour);
    }

    [Fact]
    public void Aralik_modunda_kota_daraltmasi_AYNEN_surer()
    {
        // Saatlik program kotayı gerçekten delebilir; eski koruma bozulmamalı.
        var sonuc = SubscriptionBackupScheduleService.KotayaCek(
            "interval", intervalHours: 1, hour: 3, minute: 0, dayOfWeek: 1, dayOfMonth: 1, maxBackupsPerDay: 2);

        Assert.True(sonuc.Throttled);
        Assert.Equal(12, sonuc.Plan.IntervalHours);
    }

    [Fact]
    public void Mod_YOKSA_abonelik_programi_da_eski_ARALIK_davranisinda_kalir()
    {
        var sonuc = SubscriptionBackupScheduleService.KotayaCek(
            null, intervalHours: 24, hour: 3, minute: 0, dayOfWeek: 1, dayOfMonth: 1, maxBackupsPerDay: 0);

        Assert.Equal(BackupSchedule.Interval, sonuc.Plan.Mode);
        Assert.Equal(24, sonuc.Plan.IntervalHours);
        Assert.False(sonuc.Throttled);
    }

}
