using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>Tek bir <c>Require</c> satırı.</summary>
internal sealed class HtaccessRequireEntry
{
    public string Verb { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public int Line { get; init; }
    public string File { get; init; } = string.Empty;
}

/// <summary>Bir dizinin erişim kuralları: 2.2 (<c>Order/Allow/Deny</c>) ve 2.4 (<c>Require</c>).</summary>
internal sealed class HtaccessAccessRules
{
    /// <summary><c>allow,deny</c> ya da <c>deny,allow</c>. Belirtilmediyse <c>null</c>.</summary>
    public string? Order { get; set; }

    public List<string> Allow { get; } = [];

    public List<string> Deny { get; } = [];

    public List<HtaccessRequireEntry> Require { get; } = [];

    public bool IsEmpty => Order is null && Allow.Count == 0 && Deny.Count == 0 && Require.Count == 0;
}

/// <summary>Erişim denetiminin kararı.</summary>
internal readonly record struct HtaccessAccessDecision(bool Allowed, bool NeedsAuthentication, string Reason);

/// <summary>
/// <c>Order/Allow/Deny</c> (Apache 2.2, <c>mod_access_compat</c>) ve <c>Require</c> (Apache 2.4,
/// <c>mod_authz_*</c>) değerlendirmesi.
/// </summary>
/// <remarks>
/// <para>
/// İki söz dizimi de gerçek dünyada yaygın: internetteki kalıpların çoğu hâlâ 2.2 biçiminde
/// yazılmış, yeni yazılanlar 2.4. İkisi de destekleniyor.
/// </para>
/// <para>
/// <b>İKİSİ AYNI DOSYADA olursa</b> Apache <c>mod_access_compat</c> yüklüyse ikisini birden
/// değerlendirir ve sonuç kolayca şaşırtıcı olur. Motor bu durumda <c>Require</c>'ı esas alır ve
/// UYARI üretir; sessizce birini seçmek, kullanıcının kapalı sandığı bir dizini açık bırakabilirdi.
/// </para>
/// </remarks>
internal static class HtaccessAccessControl
{
    public static HtaccessAccessDecision Evaluate(
        HtaccessAccessRules rules,
        HtaccessRequestContext request,
        IReadOnlyDictionary<string, string> env,
        bool authConfigured,
        List<HtaccessDiagnostic> bulgular)
    {
        if (rules.IsEmpty)
        {
            return new HtaccessAccessDecision(true, false, string.Empty);
        }

        if (rules.Require.Count > 0)
        {
            if (rules.Order is not null || rules.Allow.Count > 0 || rules.Deny.Count > 0)
            {
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                    HtaccessDiagnosticCodes.MixedAccessSyntax,
                    rules.Require[0].File, rules.Require[0].Line, "Require",
                    "Aynı yapılandırmada hem 'Order/Allow/Deny' (Apache 2.2) hem 'Require' (Apache 2.4) var. Motor YALNIZCA Require satırlarını uyguluyor; Apache mod_access_compat yüklüyse İKİSİNİ BİRDEN uygular ve sonuç farklı olabilir."));
            }

            return RequireDegerlendir(rules.Require, request, env, authConfigured, bulgular);
        }

        return OrderDegerlendir(rules, request, env);
    }

    private static HtaccessAccessDecision RequireDegerlendir(
        List<HtaccessRequireEntry> girdiler,
        HtaccessRequestContext request,
        IReadOnlyDictionary<string, string> env,
        bool authConfigured,
        List<HtaccessDiagnostic> bulgular)
    {
        // Apache'nin varsayılan birleştiricisi <RequireAny>'dir: BİR tanesinin geçmesi yeter.
        var kimlikGerekli = false;

        foreach (var girdi in girdiler)
        {
            switch (girdi.Verb.ToLowerInvariant())
            {
                case "all":
                    var arguman = girdi.Arguments.FirstOrDefault() ?? string.Empty;
                    if (arguman.Equals("granted", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    if (arguman.Equals("denied", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Desteklenmiyor(bulgular, girdi, $"'Require all {arguman}' tanınmıyor; yalnızca 'granted' ve 'denied' destekleniyor.");
                    continue;

                case "ip":
                case "forward-dns":
                    if (girdi.Verb.Equals("forward-dns", StringComparison.OrdinalIgnoreCase))
                    {
                        Desteklenmiyor(bulgular, girdi, "'Require forward-dns' DNS sorgusu gerektirir; uygulanmıyor.");
                        continue;
                    }

                    if (girdi.Arguments.Any(a => IpEslesiyor(a, request.RemoteAddress)))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    continue;

                case "env":
                    if (girdi.Arguments.Any(env.ContainsKey))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    continue;

                case "method":
                    if (girdi.Arguments.Any(a => a.Equals(request.Method, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    continue;

                case "valid-user":
                    if (!authConfigured)
                    {
                        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                            HtaccessDiagnosticCodes.AuthIncomplete, girdi.File, girdi.Line, "Require",
                            "'Require valid-user' var ama AuthType/AuthUserFile eksik. Apache bu durumda 500 döner; motor erişimi REDDEDER."));
                        continue;
                    }

                    if (!string.IsNullOrEmpty(request.RemoteUser))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    kimlikGerekli = true;
                    continue;

                case "user":
                    if (!authConfigured)
                    {
                        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                            HtaccessDiagnosticCodes.AuthIncomplete, girdi.File, girdi.Line, "Require",
                            "'Require user' var ama AuthType/AuthUserFile eksik. Apache bu durumda 500 döner; motor erişimi REDDEDER."));
                        continue;
                    }

                    if (!string.IsNullOrEmpty(request.RemoteUser) &&
                        girdi.Arguments.Any(a => string.Equals(a, request.RemoteUser, StringComparison.Ordinal)))
                    {
                        return new HtaccessAccessDecision(true, false, string.Empty);
                    }

                    kimlikGerekli = string.IsNullOrEmpty(request.RemoteUser);
                    continue;

                case "group":
                    Desteklenmiyor(bulgular, girdi, "'Require group' grup dosyası okumayı gerektirir (AuthGroupFile); uygulanmıyor.");
                    continue;

                case "host":
                    Desteklenmiyor(bulgular, girdi, "'Require host' ters DNS sorgusu gerektirir; uygulanmıyor. Yerine 'Require ip' kullanın.");
                    continue;

                case "expr":
                    Desteklenmiyor(bulgular, girdi, "'Require expr' Apache ifade dilini gerektirir; uygulanmıyor.");
                    continue;

                default:
                    Desteklenmiyor(bulgular, girdi, $"'Require {girdi.Verb}' tanınmıyor; uygulanmıyor.");
                    continue;
            }
        }

        return new HtaccessAccessDecision(false, kimlikGerekli,
            kimlikGerekli ? "kimlik doğrulaması gerekiyor" : "Require kuralları erişime izin vermedi");
    }

    private static HtaccessAccessDecision OrderDegerlendir(
        HtaccessAccessRules rules, HtaccessRequestContext request, IReadOnlyDictionary<string, string> env)
    {
        var sira = (rules.Order ?? "deny,allow").Replace(" ", string.Empty).ToLowerInvariant();
        var izinli = rules.Allow.Any(a => Eslesiyor(a, request, env));
        var yasakli = rules.Deny.Any(a => Eslesiyor(a, request, env));

        // Apache mod_access_compat kuralları:
        //  allow,deny -> önce Allow bakılır, sonra Deny; VARSAYILAN reddet.
        //  deny,allow -> önce Deny bakılır, sonra Allow; VARSAYILAN kabul.
        if (sira == "allow,deny")
        {
            var sonuc = izinli && !yasakli;
            return new HtaccessAccessDecision(sonuc, false, sonuc ? string.Empty : "Order allow,deny — erişim reddedildi");
        }

        var kabul = !yasakli || izinli;
        return new HtaccessAccessDecision(kabul, false, kabul ? string.Empty : "Order deny,allow — erişim reddedildi");
    }

    private static bool Eslesiyor(string spec, HtaccessRequestContext request, IReadOnlyDictionary<string, string> env)
    {
        var temiz = spec.Trim();
        if (temiz.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (temiz.StartsWith("env=", StringComparison.OrdinalIgnoreCase))
        {
            return env.ContainsKey(temiz[4..]);
        }

        return IpEslesiyor(temiz, request.RemoteAddress);
    }

    /// <summary>
    /// Apache'nin <c>Allow from</c> / <c>Require ip</c> adres biçimleri:
    /// tam adres, kısmi ön ek (<c>192.168.1</c>), CIDR (<c>10.0.0.0/8</c>), <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <b>Alan adı biçimi (<c>Allow from example.com</c>) DESTEKLENMEZ:</b> ters DNS sorgusu
    /// gerektirir, her istekte ağ gecikmesi getirir ve sahteciliğe açıktır. Eşleşmez ve kural
    /// sessizce "izin vermedi" sonucunu üretir — bulgu üreten çağıran tarafta bildirilir.
    /// </remarks>
    public static bool IpEslesiyor(string spec, string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(spec) || string.IsNullOrWhiteSpace(remoteAddress))
        {
            return false;
        }

        var temiz = spec.Trim();
        if (temiz.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(remoteAddress, out var uzak))
        {
            return false;
        }

        var boluIndex = temiz.IndexOf('/');
        if (boluIndex > 0)
        {
            return CidrIcinde(temiz[..boluIndex], temiz[(boluIndex + 1)..], uzak);
        }

        // KISMİ ÖN EK ("192.168.1" -> 192.168.1.0/24) TAM ADRESTEN ÖNCE denenmelidir.
        // ÖLÇÜLDÜ: .NET'in IPAddress.TryParse'ı eski "classful" kısayolu kabul eder ve
        // "192.168.1" dizgesini 192.168.0.1 ADRESİNE çevirir. Önce tam adres denenseydi
        // Apache'nin ön ek kuralı hiç çalışmaz, üstelik bambaşka bir adresle karşılaştırılırdı —
        // "Allow from 10.0.0" yazan bir yönetici 10.0.0.0/24'ü değil 10.0.0.0 tek adresini açmış olurdu.
        if (uzak.AddressFamily == AddressFamily.InterNetwork &&
            temiz.All(k => HtaccessPortableApi.AsciiRakamMi(k) || k == '.'))
        {
            var parcalar = temiz.TrimEnd('.').Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parcalar.Length is >= 1 and <= 3)
            {
                var tamamlanmis = string.Join(".", parcalar.Concat(Enumerable.Repeat("0", 4 - parcalar.Length)));
                return CidrIcinde(tamamlanmis, (parcalar.Length * 8).ToString(), uzak);
            }
        }

        return IPAddress.TryParse(temiz, out var tam) && tam.Equals(uzak);
    }

    private static bool CidrIcinde(string network, string prefix, IPAddress uzak)
    {
        if (!IPAddress.TryParse(network, out var ag) || !int.TryParse(prefix, out var bit))
        {
            return false;
        }

        if (ag.AddressFamily != uzak.AddressFamily)
        {
            return false;
        }

        var agBaytlari = ag.GetAddressBytes();
        var uzakBaytlari = uzak.GetAddressBytes();
        if (bit < 0 || bit > agBaytlari.Length * 8)
        {
            return false;
        }

        var tamBayt = bit / 8;
        for (var i = 0; i < tamBayt; i++)
        {
            if (agBaytlari[i] != uzakBaytlari[i])
            {
                return false;
            }
        }

        var kalan = bit % 8;
        if (kalan == 0)
        {
            return true;
        }

        var maske = (byte)(0xFF << (8 - kalan));
        return (agBaytlari[tamBayt] & maske) == (uzakBaytlari[tamBayt] & maske);
    }

    private static void Desteklenmiyor(List<HtaccessDiagnostic> bulgular, HtaccessRequireEntry girdi, string mesaj) =>
        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
            HtaccessDiagnosticCodes.UnsupportedDirective, girdi.File, girdi.Line, "Require", mesaj));
}
