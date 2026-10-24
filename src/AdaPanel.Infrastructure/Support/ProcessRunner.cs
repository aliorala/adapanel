using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Support;

internal static class ProcessRunner
{
    /// <summary>
    /// Bir dış aracın TEK bir akışından (stdout ya da stderr) belleğe alınacak EN ÇOK karakter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KAPATILAN AÇIK (ölçüldü):</b> iki akış da <c>ReadToEndAsync</c> ile SINIRSIZ okunuyordu;
    /// tek sınır zaman aşımıydı (10 dk). Bu, deponun en çok kullanılan ilkelidir ve KİRACIYA
    /// ULAŞILABİLİR: <c>FileService.SetPermissionsAsync</c> <c>icacls … /T /C</c>'yi kiracının KENDİ
    /// ağacında koşturur ve <c>/C</c> her dosya için bir satır yazar — çıktı kiracının dosya
    /// sayısıyla doğru orantılı büyür. Panel LocalSystem olduğundan bu belleği hiçbir kota
    /// sınırlamıyordu.
    /// </para>
    /// <para>
    /// <b>DEĞER NEDEN 4 MiB:</b> panelin ayrıştırdığı meşru makine çıktıları (IIS site listesi
    /// JSON'u, Stalwart NDJSON hesap listesi, DNS bölge dökümü, freshclam/clamscan özeti) kilobayt
    /// ile düşük megabayt aralığındadır; 4 MiB bunların hepsine geniş bir pay bırakır. Buna karşılık
    /// üst sınır olarak süreç başına ~8 MB bellek demektir (char = 2 bayt) ve eşzamanlı çağrılarda
    /// da hesaplanabilir kalır. Sınıra takılan tek ölçülmüş yol <c>icacls /T</c> çıktısıdır
    /// (~120 karakter/dosya → ~35.000 dosya); orada çıktı yalnız HATA METNİ olarak kullanılır,
    /// ayrıştırılmaz — yani kesilmesi bir karar değiştirmez.
    /// </para>
    /// <para>
    /// <b>KESİLME SESSİZ DEĞİLDİR:</b> kesilen akışın sonuna <see cref="TruncationNotice"/> eklenir.
    /// Sessiz kesme, teşhis çıktısına yalan söyletmek olurdu: kullanıcı eksik bir listeyi TAM
    /// sanardı.
    /// </para>
    /// </remarks>
    internal const int DefaultOutputCharLimit = 4 * 1024 * 1024;

    /// <summary>Kesilen akışın sonuna eklenen işaret; İngilizce çünkü aynı akışa yazılan diğer panel notları da öyle.</summary>
    internal const string TruncationNotice = "[adapanel] Output truncated: the tool wrote more than the capture limit and the rest was discarded.";

    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        int outputCharLimit = DefaultOutputCharLimit,
        string? standardInputFilePath = null)
    {
        // ÇALIŞMA SAĞLIĞI: çağıran açıkça timeout vermezse de güvenli bir TAVAN uygula. Aksi halde
        // asılan bir dış araç (erişilemez git remote, yanıt vermeyen DB CLI, win-acme istemi vb.)
        // isteği SONSUZA dek dondurur ve bağlantıyı kilitler. Yanıt isteyen yollar zaten kendi kısa
        // timeout'unu geçer (ör. mail 30sn); bu yalnız üst sınırdır. 10 dk; en uzun meşru tek komut
        // (büyük git clone, win-acme issuance) bile sığar ama sonsuz asılma engellenir.
        timeout ??= TimeSpan.FromMinutes(10);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // TOPLU (BATCH) GİRDİ. Bazı CLI'lar bir SQL/komut dosyasını YALNIZCA standart girdiden
            // aldıklarında hataları çıkış koduna yansıtır; dosyayı kendi istemci komutlarıyla
            // (ör. mysql'in `source`'u) okuduklarında yansıtmazlar. Bu bayrak o ayrımı mümkün kılar.
            RedirectStandardInput = standardInputFilePath is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        ConfigureWindowsPowerShellEnvironment(startInfo);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        // Zaman aşımı verildiyse süre dolunca süreci öldür ve "başarısız" sonuç dön; böylece
        // takılan dış araçlar (win-acme HTTP-01 doğrulaması, MailEnable COM vb.) isteği
        // sonsuza kadar dondurmaz.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutCts.CancelAfter(limit);
        }

        // TAVANLI OKUMA. Borular yine SONUNA KADAR tüketilir (aşağıdaki okuyucu tavanı aştıktan
        // sonra da okumaya devam eder, yalnız biriktirmez): tüketmeyi bırakmak boruyu doldurur ve
        // aracı kendi yazma çağrısında SONSUZA KADAR bloke ederdi — yani bellek sınırı, asılma
        // sınırına dönüşürdü.
        var outputTask = TavanliOkuAsync(process.StandardOutput, outputCharLimit, timeoutCts.Token, preservePartialOnCancellation: true);
        var errorTask = TavanliOkuAsync(process.StandardError, outputCharLimit, timeoutCts.Token, preservePartialOnCancellation: true);

        // GİRDİ, ÇIKTI OKUYUCULARI BAŞLADIKTAN SONRA yazılır. Ters sırada büyük bir dosyayı
        // beslemek klasik kilitlenmedir: araç çıktı borusunu doldurur, biz onu tüketmediğimiz için
        // araç bloke olur, biz de yazmayı bitiremediğimiz için beklemeye devam ederiz.
        var inputTask = standardInputFilePath is null
            ? Task.CompletedTask
            : BesleAsync(process, standardInputFilePath, timeoutCts.Token);

        try
        {
            // The deadline includes inherited pipes, even after the parent has exited.
            await Task.WhenAll(process.WaitForExitAsync(timeoutCts.Token), outputTask, errorTask)
                .WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await StopProcessAsync(process);
            await ObserveInputAsync(inputTask);

            // OKUMA GÖREVLERİ SAHİPSİZ BIRAKILMAZ.
            //
            // Bu iki görev süreç boruları üzerinde çalışıyor. Beklenmeden dönülürse `using var
            // process` boruları kapatır, görevler ObjectDisposedException/IOException ile düşer ve
            // hiç gözlemlenmedikleri için "unobserved task exception" olurlar. Zaman aşımına uğrayan
            // her dış araç çağrısı (win-acme, git clone, DB CLI) böyle bir görev + tutulan boru
            // kaynağı bırakıyordu.
            //
            // Süreç öldürüldüğü için borular kapanır ve okumalar hemen biter; yine de kısa bir
            // tavan konuyor ki bu temizlik hiçbir koşulda isteği askıya almasın. Toplanan çıktı
            // ATILMIYOR: araç zaman aşımından önce bir şey yazdıysa (çoğu zaman asıl sebep orada
            // yazılıdır) kullanıcıya gösteriliyor.
            var (kismiCikti, kismiHata) = await TopluyorAsync(outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();

            var mesaj = $"Process timed out after {timeout.Value.TotalSeconds:0} s; termination was requested.";
            return new CommandResult
            {
                ExitCode = -1,
                StandardOutput = kismiCikti,
                StandardError = string.IsNullOrWhiteSpace(kismiHata) ? mesaj : $"{mesaj}{Environment.NewLine}{kismiHata}"
            };
        }
        catch
        {
            timeoutCts.Cancel();
            await StopProcessAsync(process);
            await ObserveInputAsync(inputTask);
            await TopluyorAsync(outputTask, errorTask);
            throw;
        }

        // Cancellation can race with normal process completion.
        cancellationToken.ThrowIfCancellationRequested();

        // Besleme görevi SAHİPSİZ BIRAKILMAZ (aynı gerekçe okuma görevlerinde yazılı): gözlenmeyen
        // bir istisna sessiz kalırdı. Hata olsa bile süreç zaten bitti; sonuç çıkış kodundan gelir.
        try
        {
            await inputTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Yutulmuyor, sonuca YAZILIYOR: girdiyi tam besleyemediysek "komut çalıştı" demek
            // yarım bir işi tam göstermek olur.
            return new CommandResult
            {
                ExitCode = process.ExitCode == 0 ? -1 : process.ExitCode,
                StandardOutput = await outputTask,
                StandardError = $"Standard input could not be written: {exception.Message}{Environment.NewLine}{await errorTask}"
            };
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await outputTask,
            StandardError = await errorTask
        };
    }

    /// <summary>
    /// Bir dosyanın içeriğini sürecin standart girdisine akıtır ve borunun ucunu KAPATIR.
    /// </summary>
    /// <remarks>
    /// Borunun kapatılması şart: toplu modda çalışan araçlar (mysql, psql, sqlcmd) girdi akışı
    /// bitene kadar beklerler. Kapatılmazsa araç asılır ve yalnız zaman aşımıyla ölür.
    /// </remarks>
    private static async Task BesleAsync(Process process, string filePath, CancellationToken cancellationToken)
    {
        try
        {
        await using var kaynak = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hedef = process.StandardInput.BaseStream;
        await kaynak.CopyToAsync(hedef, cancellationToken);
        await hedef.FlushAsync(cancellationToken);
        }
        finally
        {
            // EOF must be sent even when opening or reading the input file fails.
            process.StandardInput.Close();
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* The process can exit concurrently; cleanup must remain bounded. */ }
    }

    private static async Task ObserveInputAsync(Task inputTask)
    {
        try { await inputTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch
        {
            _ = inputTask.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Akışı SONUNA KADAR tüketir ama en çok <paramref name="tavan"/> karakter BİRİKTİRİR; tavan
    /// aşılırsa sonuca <see cref="TruncationNotice"/> eklenir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tavan aşıldıktan sonra okumanın SÜRMESİ zorunludur: okumayı kesmek işletim sistemi borusunu
    /// doldurur ve dış aracı kendi <c>write</c> çağrısında bloke eder. O anda araç ne biter ne de
    /// hata verir — yalnız zaman aşımına kadar asılır. Yani "belleği koru" düzeltmesi, düzelttiğinden
    /// daha kötü bir arıza (her büyük çıktıda 10 dakikalık asılma) üretirdi.
    /// </para>
    /// <para>
    /// Tavan 0 ya da negatifse sınır UYGULANMAZ; bu yalnız çağıranın açıkça "sınırsız" dediği
    /// durumdur ve depoda böyle bir çağıran YOKTUR (varsayılan <see cref="DefaultOutputCharLimit"/>).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>internal, cunku TERMINAL de bunu kullanir.</b> Terminal kendi surecini kuruyor (kodlanmis
    /// komut + calisma dizini) ve <see cref="RunAsync"/>'ten gecmiyordu; sonuc olarak 0.8.9'da
    /// burada kapatilan "sinirsiz cikti okuma" acigi terminal yolunda ACIK KALMISTI. Metodu
    /// paylasmak, kurali ikinci kez yazmaktan iyidir -- bu depoda ikinci nushanin bedeli defalarca
    /// olculdu.
    /// </remarks>
    internal static async Task<string> TavanliOkuAsync(StreamReader reader, int tavan, CancellationToken cancellationToken,
        bool preservePartialOnCancellation = false)
    {
        if (tavan <= 0)
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }

        var tampon = new char[8192];
        var toplanan = new StringBuilder();
        var kesildi = false;

        while (true)
        {
            int okunan;
            try
            {
                okunan = await reader.ReadAsync(tampon.AsMemory(), cancellationToken);
            }
            catch (OperationCanceledException) when (preservePartialOnCancellation && cancellationToken.IsCancellationRequested)
            {
                // Terminal deadlines include output draining; retain the diagnostic prefix.
                break;
            }
            if (okunan == 0)
            {
                break;
            }

            var kalan = tavan - toplanan.Length;
            if (kalan <= 0)
            {
                kesildi = true;
                continue;
            }

            var alinacak = Math.Min(okunan, kalan);
            toplanan.Append(tampon, 0, alinacak);
            if (alinacak < okunan)
            {
                kesildi = true;
            }
        }

        if (kesildi)
        {
            toplanan.Append(Environment.NewLine).Append(TruncationNotice);
        }

        return toplanan.ToString();
    }

    /// <summary>
    /// Zaman aşımı sonrası okuma görevlerini gözlemler; ne kadar okunabildiyse onu döndürür.
    /// Görevlerin kendi hataları yutulur — burada amaç veri toplamak değil, görevi SAHİPSİZ
    /// bırakmamak.
    /// </summary>
    internal static async Task<(string Output, string Error)> TopluyorAsync(Task<string> outputTask, Task<string> errorTask,
        TimeSpan? collectionTimeout = null)
    {
        var readers = Task.WhenAll(outputTask, errorTask);
        try { await readers.WaitAsync(collectionTimeout ?? TimeSpan.FromSeconds(5)); }
        catch
        {
            // Observe a late pipe fault after bounded cleanup without holding the request.
            _ = readers.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        return (outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty,
            errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty);
    }

    /// <summary>
    /// UZUN scriptler komut satirina SIGMAZ; bu esigin ustunde gecici bir .ps1 dosyasindan
    /// calistirilir.
    /// <para>
    /// Windows'ta komut satiri 32767 karakterle sinirlidir ve -EncodedCommand base64 oldugu icin
    /// metni ~2.7 katina cikarir. Yani ~12 KB'lik bir script bile "Dosya adi veya uzantisi cok
    /// uzun" hatasiyla HIC CALISTIRILAMAZ. Canlida tam olarak bu oldu: uzun TXT icin eklenen
    /// yollar birikince bolge senkron scripti sinirı asti ve TUM kayitlar bu hatayi verdi --
    /// yani duzeltmeler birbirini bogdu.
    /// </para>
    /// </summary>
    private const int EncodedCommandLimit = 8000;

    /// <summary>
    /// Her scriptin başına eklenir: PowerShell'in çıktısını UTF-8 yazmasını sağlar.
    /// <para>
    /// KÖK NEDEN (ölçülerek bulundu): PowerShell 5.1 yönlendirilmiş çıktısını konsolun kod
    /// sayfasıyla yazar, panel ise <see cref="Encoding.UTF8"/> ile okur. İki taraf anlaşmadığı için
    /// ASCII DIŞINDAKİ HER KARAKTER yok olur. Ölçüm: <c>epey ürün çekme botu ĞÜŞİÖÇ</c> adı
    /// <c>epey ?r?n ?ekme botu ??????</c> olarak geri geldi; bu satır eklendiğinde birebir doğru döndü.
    /// </para>
    /// <para>
    /// Görünen etkisi: panel görev listesini bozuk adlarla okuyor, kullanıcı "Şimdi çalıştır"a
    /// bastığında o bozuk adı geri gönderiyor ve Görev Zamanlayıcı görevi bulamıyordu
    /// (0x80070002). Ama sorun cron'a özel DEĞİL: PowerShell'den okunan Türkçe karakterli her
    /// değer aynı şekilde bozuluyordu — site adları, dosya yolları, hata mesajları.
    /// </para>
    /// <para>
    /// Yalnızca ÇIKTI yönü düzelir. Yerli araçların (dnscmd, icacls, netsh) kendi kod sayfasında
    /// yazdığı ASCII dışı metin bu satırdan önce de bozuktu, sonra da bozuk kalır; bu ayrı ve
    /// daha dar bir sorundur.
    /// </para>
    /// </summary>
    private const string OutputEncodingPrefix = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n";

    internal static void ConfigureWindowsPowerShellEnvironment(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows() ||
            !Path.GetFileName(startInfo.FileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            return;

        // A .NET process launched from PowerShell 7 inherits Core's module paths. Windows
        // PowerShell then loads incompatible Utility/Security/Archive modules and loses
        // Get-FileHash/Get-Acl or archive errors. Prefer its own built-ins; keep custom paths.
        var builtInModules = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        startInfo.Environment.TryGetValue("PSModulePath", out var inherited);
        startInfo.Environment["PSModulePath"] = string.Join(Path.PathSeparator,
            new[] { builtInModules }.Concat((inherited ?? string.Empty).Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static async Task<CommandResult> RunPowerShellAsync(string command, CancellationToken cancellationToken = default)
    {
        var script = OutputEncodingPrefix + command;

        var result = script.Length > EncodedCommandLimit
            ? await RunPowerShellFromFileAsync(script, cancellationToken)
            : await RunAsync(
                "powershell.exe",
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
                cancellationToken: cancellationToken);

        return new CommandResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = NormalizePowerShellStream(result.StandardOutput),
            StandardError = NormalizePowerShellStream(result.StandardError)
        };
    }

    /// <summary>
    /// <see cref="RunPowerShellAsync"/>'in KATI sürümü: çok ifadeli bir script'te ARIZAYI ÇIKIŞ
    /// KODUNA yansıtır.
    /// <para>
    /// KÖK NEDEN (bu makinede ölçüldü): PowerShell'in çıkış kodu, script'in SON ifadesinin
    /// sonucudur. Bozuk bir zip üzerinde <c>Expand-Archive</c> SONLANDIRICI OLMAYAN hata verir;
    /// script akmaya devam eder ve <c>powershell.exe</c> <b>0</b> ile çıkar. Ölçüm:
    /// <c>Expand-Archive &lt;bozuk.zip&gt;; Write-Output 'DEVAM-ETTI'</c> → ekrana "DEVAM-ETTI"
    /// yazıldı, çıkış kodu <b>0</b>. Aynı script bu sarmalayıcıyla → çıkış kodu <b>1</b>.
    /// </para>
    /// <para>
    /// Görünen etkisi: indirilen arşiv bir HTML hata sayfası olduğunda site içe aktarma "başarılı"
    /// sayılıyor, sitenin kökü boşaltılıyor ve panel "tamamlandı" diyordu. Yani <c>Success</c>
    /// (yalnız çıkış kodu) çok ifadeli script'lerde TEK BAŞINA YETMEZ.
    /// </para>
    /// <para>
    /// Üç şeyi birden yapar: (1) sonlandırıcı olmayan hataları sonlandırıcıya çevirir
    /// (<c>$ErrorActionPreference='Stop'</c>), (2) hatayı yakalayıp gerçek mesajı stderr'e yazar ve
    /// 1 ile çıkar, (3) script'in ORTASINDA patlayan yerli komutun (<c>$LASTEXITCODE</c>) çıkış
    /// kodunu da yüzeye çıkarır. <b>OPT-IN'dir</b>: mevcut çağıranların davranışı DEĞİŞMEZ —
    /// depodaki bazı script'ler bilerek <c>-ErrorAction SilentlyContinue</c> ile akmaya güvenir,
    /// bunu topluca katılaştırmak kendi başına bir regresyon sınıfı olurdu.
    /// </para>
    /// <para>
    /// Yine de tek başına YETERLİ DEĞİLDİR: çıkış kodu "araç şikâyet etmedi" der, "iş oldu" demez.
    /// Çağıran ayrıca BEKLENEN ÇIKTIYI doğrulamalıdır (depodaki doğru kalıp: <c>PhpService</c>
    /// indirdiği DLL'in gerçekten orada olduğunu ayrıca kontrol eder).
    /// </para>
    /// </summary>
    public static Task<CommandResult> RunPowerShellStrictAsync(string command, CancellationToken cancellationToken = default)
    {
        var strict =
            "$ErrorActionPreference = 'Stop'\n" +
            // Önceki bir değerin yanlışlıkla hata sanılmaması için sıfırlanır.
            "$global:LASTEXITCODE = 0\n" +
            "try {\n" +
            command + "\n" +
            "} catch {\n" +
            // Gerçek sebep kullanıcıya ulaşsın: panel bu satırı hata ayrıntısı olarak gösterir.
            "  [Console]::Error.WriteLine($_.Exception.Message)\n" +
            "  exit 1\n" +
            "}\n" +
            // Yerli komut (curl, icacls, schtasks...) SON ifade değilse hatası çıkış koduna
            // yansımaz; burada elle yansıtılır.
            "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n";

        return RunPowerShellAsync(strict, cancellationToken);
    }

    /// <summary>
    /// Verilen dizinin ALTINDAN çalışan, adı <paramref name="processName"/> olan süreçleri döner.
    /// <para>
    /// KÖK NEDEN (bu kuralın tek kaynağa çıkarılma sebebi): aynı karşılaştırma panelde dört ayrı
    /// yerde elle yazılmıştı ve biri YANLIŞTI — nginx tarafı kurulum dizinini ileri eğik çizgiye
    /// çevirip ("C:/.../nginx/") sürecin ters eğik çizgili yoluyla ("C:\...\nginx\nginx.exe")
    /// karşılaştırıyordu. Bu iki dizge ASLA eşleşmez: öldürme kodu fiilen ölüydü, kaldırma
    /// "başarılı" raporlanıyor ama nginx.exe dizini kilitlemeye devam ediyordu. Aynı sınıfın
    /// <c>Replace("System32")</c> dersiyle birebir aynısı.
    /// </para>
    /// <para>
    /// <see cref="Path.GetFullPath(string)"/> her iki ayırıcıyı da Windows ayırıcısına
    /// normalleştirir; karşılaştırma bu yüzden ONUN üzerinden yapılır.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Process> EnumerateProcessesUnder(string directory, string processName)
    {
        var eslesenler = new List<Process>();
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(processName))
        {
            return eslesenler;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            var eslesti = false;
            try
            {
                // MainModule başka bir oturumdaki/64-32 bit uyuşmazlığındaki süreçte erişim hatası
                // verebilir; o süreç zaten bizim yönettiğimiz süreç değildir.
                eslesti = IsUnderDirectory(process.MainModule?.FileName, directory);
            }
            catch
            {
                eslesti = false;
            }

            if (eslesti)
            {
                eslesenler.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return eslesenler;
    }

    /// <summary>
    /// <paramref name="candidatePath"/> gerçekten <paramref name="directory"/> ağacının içinde mi?
    /// Ayırıcı yönünden ve sondaki ayırıcıdan BAĞIMSIZ karşılaştırır.
    /// <para>
    /// Sondaki ayırıcı şart: aksi halde "…\nginx" öneki "…\nginx-yedek\nginx.exe" ile de eşleşir ve
    /// KOMŞU kurulumun süreci öldürülür.
    /// </para>
    /// </summary>
    public static bool IsUnderDirectory(string? candidatePath, string directory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var kok = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var aday = Path.GetFullPath(candidatePath);
            return aday.StartsWith(kok, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Geçersiz yol karakteri vb.: "içinde değil" say — asla yanlışlıkla öldürme.
            return false;
        }
    }

    /// <summary>Dizinin altından çalışan süreçlerin sayısını döner (öldürmez).</summary>
    public static int CountProcessesUnder(string directory, string processName)
    {
        var surecler = EnumerateProcessesUnder(directory, processName);
        try
        {
            return surecler.Count;
        }
        finally
        {
            foreach (var process in surecler)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Dizinin altından çalışan süreçleri (alt süreçleriyle birlikte) öldürür; öldürülen sayısını
    /// döner. Öldürme başarısız olursa YUTULUR — çağıran sonucu <see cref="CountProcessesUnder"/>
    /// ile ÖLÇMELİDİR; "komut hata vermedi" ile "süreç gerçekten öldü" aynı şey değildir.
    /// </summary>
    public static int KillProcessesUnder(string directory, string processName)
    {
        var oldurulen = 0;
        foreach (var process in EnumerateProcessesUnder(directory, processName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                oldurulen++;
            }
            catch
            {
                // Süreç bu arada kendi kapanmış ya da yetki yok olabilir.
            }
            finally
            {
                process.Dispose();
            }
        }

        return oldurulen;
    }

    /// <summary>
    /// Scripti gecici bir dosyaya yazip <c>-File</c> ile calistirir. Uzunluk siniri kalmaz.
    /// <para>
    /// Dosya UTF-8 BOM ile yazilir: PowerShell 5.1 BOM yoksa dosyayi sistem kod sayfasiyla okur ve
    /// Turkce karakterler (alan adlarinda, mesajlarda) bozulur.
    /// </para>
    /// </summary>
    private static async Task<CommandResult> RunPowerShellFromFileAsync(string command, CancellationToken cancellationToken)
    {
        // SIR SIZINTISI: bu dosya C:\Windows\TEMP'e düşer ve oranın varsayılan DACL'i her yeni
        // dosyaya IIS_IUSRS:ReadData MİRAS ETTİRİR (ölçüldü). Buraya yazılan script, çağıranın
        // içine gömdüğü parolayı olduğu gibi taşır; yani her kiracının havuz kimliği okuyabilirdi.
        // Dosya bu yüzden AÇILIRKEN kilitlenir — bkz. SecureTempFile.
        var scriptPath = SecureTempFile.BuildPath("ada-ps", ".ps1");
        try
        {
            await SecureTempFile.WriteAsync(scriptPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

            return await RunAsync(
                "powershell.exe",
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                cancellationToken: cancellationToken);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* en iyi caba */ }
        }
    }

    internal static string NormalizePowerShellStream(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        var xmlStart = normalized.IndexOf("<Objs", StringComparison.Ordinal);
        if (xmlStart >= 0)
        {
            try
            {
                var document = XDocument.Parse(normalized[xmlStart..]);
                var errorLines = document
                    .Descendants()
                    .Where(element =>
                        element.Name.LocalName == "S" &&
                        string.Equals((string?)element.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                    .Select(element => DecodeCliXmlText(element.Value))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                var messages = document.Root?.Elements()
                    .Where(element => !string.Equals((string?)element.Attribute("S"), "progress", StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];
                if (messages.All(element => element.Name.LocalName == "S"))
                {
                    normalized = normalized[..xmlStart].Replace("#< CLIXML", string.Empty).Trim();
                    var text = string.Join(Environment.NewLine, messages.Select(element => DecodeCliXmlText(element.Value)));
                    normalized = string.IsNullOrWhiteSpace(normalized) ? text : normalized + Environment.NewLine + text;
                }
                else if (errorLines.Length > 0)
                {
                    normalized = string.Join(Environment.NewLine, errorLines);
                }
            }
            catch
            {
                normalized = normalized[xmlStart..];
            }
        }

        return DecodeCliXmlText(normalized)
            .Replace("#< CLIXML", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string DecodeCliXmlText(string value)
    {
        return Regex.Replace(
            value,
            "_x([0-9A-Fa-f]{4})_",
            match => char.ConvertFromUtf32(Convert.ToInt32(match.Groups[1].Value, 16)));
    }
}
