using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Support;

internal static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null)
    {
        // ÇALIŞMA SAĞLIĞI: çağıran açıkça timeout vermezse de güvenli bir TAVAN uygula. Aksi halde
        // asılan bir dış araç (erişilemez git remote, yanıt vermeyen DB CLI, win-acme istemi vb.)
        // isteği SONSUZA dek dondurur ve bağlantıyı kilitler. Yanıt isteyen yollar zaten kendi kısa
        // timeout'unu geçer (ör. mail 30sn); bu yalnız üst sınırdır. 10 dk; en uzun meşru tek komut
        // (büyük git clone, win-acme issuance) bile sığar ama sonsuz asılma engellenir.
        timeout ??= TimeSpan.FromMinutes(10);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeout is not null && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* en iyi çaba */ }

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

            var mesaj = $"Process timed out after {timeout.Value.TotalSeconds:0} s and was terminated.";
            return new CommandResult
            {
                ExitCode = -1,
                StandardOutput = kismiCikti,
                StandardError = string.IsNullOrWhiteSpace(kismiHata) ? mesaj : $"{mesaj}{Environment.NewLine}{kismiHata}"
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
    /// Zaman aşımı sonrası okuma görevlerini gözlemler; ne kadar okunabildiyse onu döndürür.
    /// Görevlerin kendi hataları yutulur — burada amaç veri toplamak değil, görevi SAHİPSİZ
    /// bırakmamak.
    /// </summary>
    private static async Task<(string Output, string Error)> TopluyorAsync(Task<string> outputTask, Task<string> errorTask)
    {
        try
        {
            var tamamlanan = await Task.WhenAny(
                Task.WhenAll(outputTask, errorTask),
                Task.Delay(TimeSpan.FromSeconds(5)));

            if (tamamlanan is not Task<string[]>)
            {
                // 5 sn içinde bitmediler; yine de gözlemlenmiş sayılsınlar ki süreç düzeyinde
                // "unobserved exception" olayı tetiklenmesin.
                _ = outputTask.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                _ = errorTask.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                return (string.Empty, string.Empty);
            }
        }
        catch
        {
            // Borular kapandığı için okuma düşmüş olabilir; bu beklenen bir durum.
        }

        return (Guvenli(outputTask), Guvenli(errorTask));

        static string Guvenli(Task<string> task)
        {
            try
            {
                return task.IsCompletedSuccessfully ? task.Result : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
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
    /// Scripti gecici bir dosyaya yazip <c>-File</c> ile calistirir. Uzunluk siniri kalmaz.
    /// <para>
    /// Dosya UTF-8 BOM ile yazilir: PowerShell 5.1 BOM yoksa dosyayi sistem kod sayfasiyla okur ve
    /// Turkce karakterler (alan adlarinda, mesajlarda) bozulur.
    /// </para>
    /// </summary>
    private static async Task<CommandResult> RunPowerShellFromFileAsync(string command, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ada-ps-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

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

    private static string NormalizePowerShellStream(string value)
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

                if (errorLines.Length > 0)
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
