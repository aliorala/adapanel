using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Testler için GERÇEK dosyalardan oluşan geçici bir site kökü.
/// </summary>
/// <remarks>
/// Sahte bir dosya sistemi yerine gerçek disk kullanılıyor: <c>-f</c> ve <c>-d</c> koşulları,
/// dizin basamaklandırması ve <c>AuthUserFile</c> kapsama denetimi ancak gerçek yollarla
/// anlamlı biçimde ölçülebilir. Bu depoda "sahte dünyada yeşil, gerçek dünyada ölü" kodun
/// bedeli birden çok kez ödendi.
/// </remarks>
internal sealed class HtaccessTestSite : IDisposable
{
    public HtaccessTestSite()
    {
        Root = Path.Combine(Path.GetTempPath(), $"adapanel-htaccess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Engine = new HtaccessEngine();
    }

    public string Root { get; }

    public HtaccessEngine Engine { get; }

    /// <summary>Site köküne göre bir dosya yazar; ara klasörleri açar.</summary>
    public string Write(string goreliYol, string icerik)
    {
        var tam = Path.Combine(Root, goreliYol.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tam)!);
        File.WriteAllText(tam, icerik);
        Engine.InvalidateCache(Root);
        return tam;
    }

    public void Mkdir(string goreliYol) =>
        Directory.CreateDirectory(Path.Combine(Root, goreliYol.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Kök dizine <c>.htaccess</c> yazar.</summary>
    public string Htaccess(string icerik) => Write(".htaccess", icerik);

    public HtaccessRequestContext Request(
        string yol,
        string sorgu = "",
        string yontem = "GET",
        bool https = false,
        string host = "ornek.com",
        string uzakAdres = "203.0.113.10",
        IReadOnlyDictionary<string, string>? basliklar = null,
        string kullanici = "",
        HtaccessLimits? sinirlar = null) => new()
    {
        SiteRootPath = Root,
        RequestPath = yol,
        QueryString = sorgu,
        Method = yontem,
        IsHttps = https,
        HttpHost = host,
        ServerName = host,
        ServerPort = https ? 443 : 80,
        RemoteAddress = uzakAdres,
        RemoteUser = kullanici,
        Headers = basliklar ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Limits = sinirlar
    };

    public HtaccessEvaluationResult Evaluate(string yol, string sorgu = "") => Engine.Evaluate(Request(yol, sorgu));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici klasörün silinememesi testin sonucunu değiştirmez.
        }
    }
}
