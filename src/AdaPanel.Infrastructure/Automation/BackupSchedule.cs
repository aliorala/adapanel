namespace AdaPanel.Infrastructure.Automation;

/// <summary>
/// Bir yedek programının NORMALİZE edilmiş hâli: "ne zaman çalışacak" sorusunun bütün girdileri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı ve saf bir tip:</b> otomatik yedek servisi on dokuz bağımlılıkla kuruluyor.
/// "Sıra bende mi" kararını oradan ölçmek hepsini taklit etmeyi gerektirirdi; bu yüzden karar
/// buraya, saat/gün aritmetiğinden başka hiçbir şeye dokunmayan bir yere çıkarıldı. Aynı karar
/// hem sunucu geneli program (<c>AutomationService</c>) hem de abonelik başına program
/// (<c>SubscriptionBackupScheduleService</c>) tarafından kullanılır — iki ayrı kopya olsaydı biri
/// düzeltilip diğeri unutulurdu.
/// </para>
/// </remarks>
/// <param name="Mode">
/// <c>interval</c> (eski davranış: son çalışmadan N saat sonra) | <c>daily</c> | <c>weekly</c> |
/// <c>monthly</c>.
/// </param>
/// <param name="IntervalHours">Yalnızca <c>interval</c> modunda anlamlı.</param>
/// <param name="Hour">Takvim modlarında çalışma saati — <b>SUNUCUNUN YEREL SAATİ</b> (0-23).</param>
/// <param name="Minute">Takvim modlarında çalışma dakikası (0-59).</param>
/// <param name="DayOfWeek">Haftalık modda gün: 0 = Pazar … 6 = Cumartesi (<see cref="System.DayOfWeek"/> ile aynı).</param>
/// <param name="DayOfMonth">
/// Aylık modda ayın günü (1-31). Ayda o gün yoksa (ör. Şubat'ta 31) o ayın SON günü kullanılır —
/// aksi hâlde "her ayın 31'i" seçen bir kullanıcı yılın yarısında sessizce yedeksiz kalırdı.
/// </param>
internal sealed record BackupSchedulePlan(
    string Mode,
    int IntervalHours,
    int Hour,
    int Minute,
    int DayOfWeek,
    int DayOfMonth,
    int? SecondHour = null,
    int SecondMinute = 0)
{
    public bool HasSecondTime => Mode == BackupSchedule.Daily && SecondHour.HasValue &&
        (SecondHour.Value != Hour || SecondMinute != Minute);

    /// <summary>Belirli bir SAATE bağlı mı (takvim), yoksa "son çalışmadan N saat sonra" mı.</summary>
    public bool Takvimli => Mode is BackupSchedule.Daily or BackupSchedule.Weekly or BackupSchedule.Monthly;

    /// <summary>
    /// Nominal dönem: iki tur arasında beklenen süre. Aylık için 30 gün kabul edilir; bu değer
    /// yalnızca telafi penceresini ölçeklemek için kullanılır, turun kendisi gerçek takvimden
    /// hesaplanır.
    /// </summary>
    public TimeSpan Donem => Mode switch
    {
        BackupSchedule.Daily => TimeSpan.FromDays(1),
        BackupSchedule.Weekly => TimeSpan.FromDays(7),
        BackupSchedule.Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(Math.Max(1, IntervalHours))
    };

    /// <summary>
    /// TELAFİ PENCERESİ: kaçırılmış bir tur en fazla bu kadar geç kalmışsa hâlâ çalıştırılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden dönemin yarısı (sabit bir sayı değil):</b> pencere "kaçırılan an hâlâ bir sonraki
    /// andan daha mı yakın?" sorusunun ta kendisidir. Üç şeyi aynı anda garanti eder:
    /// </para>
    /// <list type="number">
    /// <item>İki tur asla dönemin yarısından yakın aralıkla çalışmaz — sunucu 02:55'te açılınca
    /// önce telafi turu, beş dakika sonra 03:00 turu koşmaz.</item>
    /// <item>Atlama maliyeti sınırlıdır: bir tur atlandıysa bir sonraki tur en fazla yarım dönem
    /// uzaktadır (günlükte 12 saat).</item>
    /// <item>Kendini ölçekler: seyrek programda atlamak pahalıdır, o yüzden daha uzun gecikmeye
    /// katlanır (haftalıkta 3,5 gün); sık programda zaten bir sonraki tur yakındır.</item>
    /// </list>
    /// <para>
    /// Ayarlanabilir bir eşik yapılmadı: kullanıcıya açıklanamayan her ayar yanlış ayarlanır ve
    /// bu eşiğin yanlış ayarlanması "yedek alınmadı" demektir.
    /// </para>
    /// </remarks>
    public TimeSpan TelafiPenceresi => Donem / 2;
}

/// <summary>
/// "Sıradaki yedek ne zaman" kararının TEK kaynağı. Saf: saat okumaz, dosya okumaz, servis çağırmaz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var (gerçek arıza):</b> otomatik yedek yalnızca ARALIK biliyordu —
/// <c>son çalışma + N saat</c>. Kullanıcı 17 Ağustos 23:50'de bir kez elle "Şimdi yedekle"
/// dediğinde gecelik yedeğin saati o ana KİLİTLENDİ ve bir sonraki tur 18 Ağustos 23:50'ye
/// düştü. Aynı sebeple panelin her yeniden başlatılması, gecikmiş her tur ve elle alınan her
/// yedek saati bir miktar ileri kaydırıyordu: yedek er ya da geç trafiğin en yoğun olduğu saate
/// oturuyordu. Yedek işi diski ve CPU'yu meşgul eder; ne zaman koştuğu bir tercih olmalıdır.
/// </para>
/// <para>
/// <b>Plesk'in yöntemi kopyalandı</b> (<c>plesk bin scheduled-backup</c>): tek bir sıklık seçici
/// (<c>hourly | daily | weekly | monthly</c>), saatlikte bir PERİYOT (<c>-backup-hourly-period</c>),
/// diğerlerinde bir SAAT (<c>-backup-time HH:MM</c>), haftalıkta gün (<c>-backup-weekday</c>),
/// aylıkta ayın günü (<c>-backup-day</c>). AdaPanel'de "hourly" yerine mevcut ve daha esnek olan
/// <c>interval</c> (kaç saatte bir) modu korundu; böylece eski kurulumlar hiç değişmeden
/// çalışmaya devam eder ve "6 saatte bir" isteyen kullanıcı da karşılığını bulur.
/// </para>
/// <para>
/// <b>YEREL SAAT — ne saklanır:</b> kullanıcı "03:00" derken kendi duvar saatini kasteder. Bu
/// yüzden kayda giren şey DUVAR SAATİDİR (saat + dakika), UTC damgası ya da UTC ofseti DEĞİL.
/// Ofset saklansaydı sunucunun saat dilimi değiştiğinde (ya da yaz saati uygulaması geldiğinde)
/// yedek duvar saatinde kayardı. Duvar saati saklandığı için "03:00" her koşulda 03:00 kalır ve
/// UTC karşılığı her tur o günün geçerli ofsetiyle YENİDEN hesaplanır. Bu, panelin başka bir
/// yerinde verilmiş karara birebir uyar: yedek gün klasörü YEREL tarihtir, dosya adındaki damga
/// UTC'dir (bkz. <c>BackupLayout</c>).
/// </para>
/// <para>
/// <b>Saat dilimi değişirse:</b> sunucunun saat dilimi <see cref="TimeZoneInfo"/> üzerinden HER
/// hesapta yeniden okunur; yeni dilimde de "03:00" yerel 03:00 demektir. Değişimin olduğu tek
/// günde tur bir kez erken ya da geç düşebilir — ama <b>atlanmaz</b>: bir sonraki uygun an her
/// zaman hesaplanır ve "son çalışma" damgası UTC olduğu için iki kez de çalışmaz.
/// </para>
/// </remarks>
internal static class BackupSchedule
{
    public const string Interval = "interval";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";

    /// <summary>Ekranda ve API'de kabul edilen mod adları.</summary>
    public static readonly string[] Modes = [Interval, Daily, Weekly, Monthly];

    /// <summary>
    /// Ham (kullanıcıdan/dosyadan gelen) değerleri güvenli aralığa çeker.
    /// </summary>
    /// <remarks>
    /// <b>Bilinmeyen mod = <see cref="Interval"/>:</b> geriye dönük uyumun taşıyıcısı budur. Eski
    /// <c>automation.json</c> dosyalarında mod alanı hiç yoktur; boş/eksik değer eski ARALIK
    /// davranışına düşer ve mevcut kurulumlarda hiçbir şey değişmez. Sessiz ve gerilemesiz geçiş
    /// bu tek satırdadır.
    /// </remarks>
    public static BackupSchedulePlan Normalize(
        string? mode,
        int intervalHours,
        int hour,
        int minute,
        int dayOfWeek,
        int dayOfMonth,
        int? secondHour = null,
        int secondMinute = 0)
    {
        var temizMod = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (!Modes.Contains(temizMod))
        {
            temizMod = Interval;
        }

        return new BackupSchedulePlan(
            temizMod,
            Math.Clamp(intervalHours, 1, 24 * 30),
            Math.Clamp(hour, 0, 23),
            Math.Clamp(minute, 0, 59),
            Math.Clamp(dayOfWeek, 0, 6),
            Math.Clamp(dayOfMonth, 1, 31),
            secondHour.HasValue ? Math.Clamp(secondHour.Value, 0, 23) : null,
            Math.Clamp(secondMinute, 0, 59));
    }

    /// <summary>
    /// KARAR: şu an bu programın turu çalıştırılmalı mı?
    /// </summary>
    /// <param name="plan">Normalize edilmiş program.</param>
    /// <param name="lastRunUtc">Son çalışmanın UTC damgası (hiç çalışmadıysa <c>null</c>).</param>
    /// <param name="nowUtc">Şimdi (UTC).</param>
    /// <param name="zone">Sunucunun saat dilimi.</param>
    public static bool ZamaniGeldi(BackupSchedulePlan plan, DateTime? lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (!plan.Takvimli)
        {
            // ESKİ DAVRANIŞ, BİREBİR: son çalışmadan N saat sonra. Tek satır bile değiştirilmedi;
            // saat seçmemiş kurulumlarda yükseltme sonrası hiçbir fark olmamalı.
            return lastRunUtc is null || nowUtc >= lastRunUtc.Value.AddHours(Math.Max(1, plan.IntervalHours));
        }

        var kacirilan = OncekiZaman(plan, nowUtc, zone);
        if (kacirilan is null)
        {
            return false;
        }

        // Bu tur ZATEN alındı mı? "Şimdi yedekle" düğmesi de bu damgayı günceller; böylece elle
        // alınan bir yedek o günün turunu karşılar ve iki saat sonra bir daha alınmaz. Aynı
        // kontrol, panel açılışta arka arkaya birkaç tur atsa bile ikinci kez tetiklenmesini
        // engeller: telafi turu koştuğu anda damga kaçırılan anın ilerisine geçer.
        if (lastRunUtc is not null && lastRunUtc.Value >= kacirilan.Value)
        {
            return false;
        }

        // KAÇIRILAN TUR TELAFİ EDİLİR: sunucu 03:00'te kapalıysa açılınca hemen alınır, ertesi
        // günü beklemez — yoksa bir gün tamamen yedeksiz kalınırdı. Ama sınırsız değil: çok geç
        // kalınmışsa (bkz. TelafiPenceresi) tur atlanır ve sıradaki ana bırakılır.
        // İki saat arasındaki aralık eşit olmak zorunda değil. Telafi penceresi bir sonraki
        // gerçek saate olan mesafenin yarısıdır; ikinci saate hemen önce çift iş başlatılmaz.
        var pencere = plan.HasSecondTime && SonrakiPlanliZaman(plan, kacirilan.Value, zone) is { } sonraki
            ? (sonraki - kacirilan.Value) / 2
            : plan.TelafiPenceresi;
        // Workers poll once a minute; a shorter window can expire between two healthy polls.
        if (pencere < TimeSpan.FromMinutes(1)) pencere = TimeSpan.FromMinutes(1);
        return nowUtc - kacirilan.Value <= pencere;
    }

    /// <summary>
    /// <paramref name="nowUtc"/> anında ya da ondan ÖNCE gelen en son planlı an (UTC).
    /// Aralık modunda takvim yoktur; <c>null</c> döner.
    /// </summary>
    public static DateTime? OncekiZaman(BackupSchedulePlan plan, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (!plan.Takvimli)
        {
            return null;
        }

        var yerelBugun = YerelTarih(nowUtc, zone);
        // Geriye kaç gün bakılacağı moda göre: haftalıkta bir haftadan fazlası, aylıkta iki ay
        // yeterlidir. Sabit bir sayı yerine moda bağlamak, "ayın 31'i" gibi seyrek günlerde
        // yanlışlıkla "hiç tur yok" sonucunu engeller.
        var geriGun = GeriyeBakisGunu(plan.Mode);
        for (var i = 0; i <= geriGun; i++)
        {
            var tarih = yerelBugun.AddDays(-i);
            if (!GunUyuyor(plan, tarih))
            {
                continue;
            }

            foreach (var an in GunSaatleri(plan, tarih, zone).OrderDescending())
            {
                if (an <= nowUtc) return an;
            }
        }

        return null;
    }

    /// <summary>
    /// Ekranda gösterilecek "sonraki çalışma" anı (UTC).
    /// </summary>
    /// <remarks>
    /// Kaçırılmış ama hâlâ telafi penceresinde olan bir tur varsa cevap <b>şimdi</b>dir. Yarının
    /// saatini göstermek, panelin birazdan yedek alacağı gerçeğini gizlerdi.
    /// </remarks>
    public static DateTime? SonrakiZaman(BackupSchedulePlan plan, DateTime? lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (!plan.Takvimli)
        {
            return (lastRunUtc ?? nowUtc).AddHours(Math.Max(1, plan.IntervalHours));
        }

        if (ZamaniGeldi(plan, lastRunUtc, nowUtc, zone))
        {
            return nowUtc;
        }

        return SonrakiPlanliZaman(plan, nowUtc, zone);
    }

    private static DateTime? SonrakiPlanliZaman(BackupSchedulePlan plan, DateTime nowUtc, TimeZoneInfo zone)
    {
        var yerelBugun = YerelTarih(nowUtc, zone);
        var ileriGun = GeriyeBakisGunu(plan.Mode);
        for (var i = 0; i <= ileriGun; i++)
        {
            var tarih = yerelBugun.AddDays(i);
            if (!GunUyuyor(plan, tarih))
            {
                continue;
            }

            foreach (var an in GunSaatleri(plan, tarih, zone).Order())
            {
                if (an > nowUtc) return an;
            }
        }

        return null;
    }

    private static IEnumerable<DateTime> GunSaatleri(BackupSchedulePlan plan, DateTime tarih, TimeZoneInfo zone)
    {
        yield return YerelDenUtc(tarih.AddHours(plan.Hour).AddMinutes(plan.Minute), zone);
        if (plan.HasSecondTime)
            yield return YerelDenUtc(tarih.AddHours(plan.SecondHour!.Value).AddMinutes(plan.SecondMinute), zone);
    }

    private static int GeriyeBakisGunu(string mode) => mode switch
    {
        Weekly => 14,
        Monthly => 70,
        _ => 2
    };

    /// <summary>
    /// Verilen YEREL tarih bu programın gününe uyuyor mu.
    /// </summary>
    private static bool GunUyuyor(BackupSchedulePlan plan, DateTime yerelTarih) => plan.Mode switch
    {
        Weekly => (int)yerelTarih.DayOfWeek == plan.DayOfWeek,
        // Ayın 31'i seçilmişse ve ay 30 gün çekiyorsa AYIN SON GÜNÜ kullanılır. Aksi hâlde
        // program yılın yarısında hiç çalışmaz ve kimse sebebini anlamaz.
        Monthly => yerelTarih.Day == Math.Min(plan.DayOfMonth, DateTime.DaysInMonth(yerelTarih.Year, yerelTarih.Month)),
        _ => true
    };

    private static DateTime YerelTarih(DateTime nowUtc, TimeZoneInfo zone)
    {
        var yerel = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        return DateTime.SpecifyKind(yerel.Date, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Bir YEREL duvar saatini UTC'ye çevirir — yaz saati geçişlerinde de.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>İlkbahar (saat ileri alınır):</b> o gün bazı duvar saatleri HİÇ YAŞANMAZ (ör. saat
    /// 02:00'den 03:00'e atlarsa 02:30 yoktur). <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>
    /// böyle bir değerde <b>istisna fırlatır</b> — yani önlem alınmasaydı o gün otomasyon turu
    /// bir hatayla düşerdi. Tur atlamak yerine saat, atlamanın kendisi kadar ileri kaydırılır:
    /// iş "var olur olmaz" çalışır.
    /// </para>
    /// <para>
    /// <b>Sonbahar (saat geri alınır):</b> aynı duvar saati İKİ KEZ yaşanır. .NET belirsiz
    /// saatlerde STANDART ofseti kullanır, yani her zaman İKİNCİ (geç) anı verir. Karar
    /// deterministik olduğu için tur bir kez düşer; birinci geçişte "henüz gelmedi", ikincide
    /// "geldi" der ve son çalışma damgası tekrarı zaten keser.
    /// </para>
    /// </remarks>
    private static DateTime YerelDenUtc(DateTime yerel, TimeZoneInfo zone)
    {
        var belirsizKind = DateTime.SpecifyKind(yerel, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(belirsizKind))
        {
            belirsizKind = belirsizKind.Add(GecisFarki(belirsizKind, zone));
        }

        return TimeZoneInfo.ConvertTimeToUtc(belirsizKind, zone);
    }

    /// <summary>Yaz saatine geçişte saatin ne kadar ileri atladığı (neredeyse her yerde bir saat).</summary>
    private static TimeSpan GecisFarki(DateTime yerel, TimeZoneInfo zone)
    {
        foreach (var kural in zone.GetAdjustmentRules())
        {
            if (yerel.Date >= kural.DateStart && yerel.Date <= kural.DateEnd && kural.DaylightDelta > TimeSpan.Zero)
            {
                return kural.DaylightDelta;
            }
        }

        return TimeSpan.FromHours(1);
    }
}
