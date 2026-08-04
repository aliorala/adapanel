namespace AdaPanel.Core.Dtos.Scheduler;

public sealed class ScheduledTaskDto
{
    public string Name { get; set; } = string.Empty;

    public string ScheduleType { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    /// <summary>Kayıtlı eylemden türetilir: "url" (curl), "php" (php.exe) ya da "command".</summary>
    public string TaskType { get; set; } = "command";

    /// <summary>url tipinde çağrılan adres (eylem argümanlarından çözülür).</summary>
    public string Url { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTime? NextRunTimeUtc { get; set; }

    public DateTime? LastRunTimeUtc { get; set; }

    /// <summary>Son çalışmanın çıkış kodu (0 = başarılı). Hiç çalışmadıysa/koşuyorsa Task Scheduler özel kodları döner.</summary>
    public long? LastTaskResult { get; set; }

    /// <summary>
    /// Görevin çıktısı bir dosyaya yazılıyor mu. Bu sürümden önce kaydedilmiş görevlerde false olur;
    /// panel bunu görüp kullanıcıya çıktı kaydını açmayı önerebilir.
    /// </summary>
    public bool CapturesOutput { get; set; }

    // Aşağıdakiler DÜZENLEME içindir.
    //
    // Bu alanlar okunmadan bir görevi düzenlemek, dokunulmayan her ayarı SESSİZCE varsayılana
    // döndürürdü: 6 saatte bir çalışan bir görev 1 saate, 03:30'daki bir yedek 00:00'a, sınırlı
    // kimlikle çalışan bir betik SYSTEM'e düşerdi. Kullanıcı yalnızca komutu değiştirdiğini sanıp
    // aslında görevi baştan tanımlamış olurdu — bu oturumda tekrar tekrar ayıkladığımız hata
    // sınıfının aynısı: panelin kendi varsayılanını kullanıcının ayarı sanması.

    /// <summary>Saat/dakika tipinde tekrar aralığı (ör. 6 = altı saatte bir).</summary>
    public int Interval { get; set; } = 1;

    /// <summary>
    /// Windows'un sakladığı HAM tekrar aralığı (ISO 8601: "PT5M", "PT1H30M", "P1D").
    /// <see cref="ScheduleType"/> ve <see cref="Interval"/> bundan türetilir.
    /// </summary>
    public string RepetitionInterval { get; set; } = string.Empty;

    /// <summary>
    /// Tetikleyici panelin tanıdığı biçimlerden hiçbirine uymadı. Arayüz bunu söylemeli: aksi
    /// hâlde kullanıcı uydurulmuş bir zamanlamayı gerçek sanır ve kaydedince onu KALICI yapar.
    /// </summary>
    public bool ScheduleUnknown { get; set; }

    /// <summary>Günlük tipinde çalışma saati ("HH:mm").</summary>
    public string TimeOfDay { get; set; } = "00:00";

    /// <summary>php tipinde kayıtlı php.exe yolundan çözülen sürüm (ör. "8.3").</summary>
    public string PhpVersion { get; set; } = string.Empty;

    /// <summary>"system" ya da "limited" — görevin kayıtlı kimliğinden çözülür.</summary>
    public string RunAs { get; set; } = "system";

    /// <summary>Tek çalışmanın süre sınırı (saat). 0 = sınırsız.</summary>
    public int TimeLimitHours { get; set; }

    /// <summary>Windows'un sakladığı HAM süre sınırı ("PT0S" = sınırsız, "P1D" = 24 saat).</summary>
    public string ExecutionTimeLimit { get; set; } = string.Empty;
}
