using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// Başarısız giriş denemelerini IP bazında sayar ve eşik aşıldığında Windows
/// Firewall'da süreli engel kuralı oluşturur (fail2ban benzeri). Durum
/// <c>config/bruteforce.json</c> dosyasında tutulur.
/// </summary>
public sealed class BruteForceProtectionService(
    JsonFileStore fileStore,
    IConcurrencyLock concurrencyLock,
    ILogger<BruteForceProtectionService> logger) : IBruteForceProtectionService
{
    private const string StatePath = "config/bruteforce.json";
    private const string RulePrefix = "AdaPanel BruteForce Block ";

    public async Task<BruteForceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return ToStatus(state);
    }

    public async Task<bool> IsBannedAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        var ip = Normalize(ipAddress);
        if (ip is null)
        {
            return false;
        }

        // Kilit ALINMAZ: bu, her giriş denemesinde çalışan sıcak bir yol ve global kilit panelin
        // tamamıyla paylaşılıyor. Salt okuma için kilit almak, uzun süren bir iş (yedek yüklemesi
        // gibi) kilidi tuttuğunda giriş ekranını da kilitlerdi.
        var state = await LoadAsync(cancellationToken);
        if (!state.Enabled || IsWhitelisted(state, ip))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        return state.Bans.Any(ban =>
            string.Equals(ban.IpAddress, ip, StringComparison.OrdinalIgnoreCase) &&
            (ban.ExpiresAtUtc is null || ban.ExpiresAtUtc > now));
    }

    public async Task<BruteForceStatusDto> UpdateSettingsAsync(UpdateBruteForceSettingsRequest request, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Auth, async () =>
        {
            var state = await LoadAsync(cancellationToken);
            state.Enabled = request.Enabled;
            state.MaxFailedAttempts = Math.Clamp(request.MaxFailedAttempts, 1, 1000);
            state.WindowMinutes = Math.Clamp(request.WindowMinutes, 1, 1440);
            state.BanMinutes = Math.Clamp(request.BanMinutes, 0, 525600);
            state.WhitelistedIps = NormalizeWhitelist(request.WhitelistedIps);
            await SaveAsync(state, cancellationToken);
            return ToStatus(state);
        }, cancellationToken);
    }

    public async Task RegisterFailedAttemptAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        var ip = Normalize(ipAddress);
        if (ip is null)
        {
            return;
        }

        try
        {
            string? ipToBan = null;
            BanRecord? banRecord = null;

            await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Auth, async () =>
            {
                var state = await LoadAsync(cancellationToken);
                if (!state.Enabled || IsWhitelisted(state, ip) || state.Bans.Any(ban => ban.IpAddress == ip))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var window = TimeSpan.FromMinutes(Math.Max(1, state.WindowMinutes));
                var attempt = state.Attempts.FirstOrDefault(item => item.IpAddress == ip);
                if (attempt is null || now - attempt.FirstSeenUtc > window)
                {
                    attempt = new AttemptRecord { IpAddress = ip, FirstSeenUtc = now, Count = 0 };
                    state.Attempts.RemoveAll(item => item.IpAddress == ip);
                    state.Attempts.Add(attempt);
                }

                attempt.Count++;
                attempt.LastSeenUtc = now;

                if (attempt.Count >= state.MaxFailedAttempts)
                {
                    var ban = new BanRecord
                    {
                        IpAddress = ip,
                        BannedAtUtc = now,
                        ExpiresAtUtc = state.BanMinutes <= 0 ? null : now.AddMinutes(state.BanMinutes),
                        FailedAttempts = attempt.Count,
                        FirewallRuleName = RulePrefix + ip
                    };

                    state.Bans.Add(ban);
                    state.Attempts.RemoveAll(item => item.IpAddress == ip);
                    ipToBan = ip;
                    banRecord = ban;
                }

                await SaveAsync(state, cancellationToken);
            }, cancellationToken);

            if (ipToBan is not null && banRecord is not null)
            {
                var blocked = await ApplyFirewallBlockAsync(banRecord, cancellationToken);
                if (blocked)
                {
                    logger.LogWarning("Brute-force eşiği aşıldı, IP engellendi: {IpAddress}", ipToBan);
                }
                else
                {
                    // Kural yazılamadı (firewall servisi kapalı, grup ilkesiyle yönetilen firewall,
                    // yetersiz yetki...). Ban kaydı DURUR — çünkü asıl zorlayıcı katman artık
                    // uygulama tarafındaki IsBannedAsync denetimidir ve o firewall'dan bağımsız
                    // çalışır. Burada yapılması gereken tek şey durumu DÜRÜST raporlamak: sessizce
                    // "engellendi" demek, yöneticiye var olmayan bir koruma vaat ederdi.
                    logger.LogError(
                        "Brute-force: {IpAddress} için firewall kuralı YAZILAMADI. IP ağ katmanında " +
                        "engellenmedi; panel girişi uygulama katmanında reddedilmeye devam edecek. " +
                        "Windows Firewall servisini ve yönetici yetkisini kontrol edin.",
                        ipToBan);
                }
            }
        }
        catch (Exception exception)
        {
            // Koruma katmanı asla giriş akışını bozmamalı.
            logger.LogError(exception, "Brute-force başarısız giriş kaydı sırasında hata.");
        }
    }

    public async Task RegisterSuccessAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        var ip = Normalize(ipAddress);
        if (ip is null)
        {
            return;
        }

        try
        {
            await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Auth, async () =>
            {
                var state = await LoadAsync(cancellationToken);
                var removed = state.Attempts.RemoveAll(item => item.IpAddress == ip);
                if (removed > 0)
                {
                    await SaveAsync(state, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Brute-force başarılı giriş temizliği sırasında hata.");
        }
    }

    public async Task<BruteForceStatusDto> UnbanAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var ip = Normalize(ipAddress) ?? throw new AppException("security.bruteforce.invalid_ip", 400, 400);

        BanRecord? removedBan = null;
        var status = await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Auth, async () =>
        {
            var state = await LoadAsync(cancellationToken);
            removedBan = state.Bans.FirstOrDefault(ban => ban.IpAddress == ip);
            state.Bans.RemoveAll(ban => ban.IpAddress == ip);
            state.Attempts.RemoveAll(item => item.IpAddress == ip);
            await SaveAsync(state, cancellationToken);
            return ToStatus(state);
        }, cancellationToken);

        if (removedBan is not null)
        {
            await RemoveFirewallBlockAsync(removedBan.FirewallRuleName, cancellationToken);
        }

        return status;
    }

    public async Task CleanupExpiredBansAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var expired = new List<BanRecord>();
            await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Auth, async () =>
            {
                var state = await LoadAsync(cancellationToken);
                var now = DateTime.UtcNow;
                expired = state.Bans.Where(ban => ban.ExpiresAtUtc is not null && ban.ExpiresAtUtc <= now).ToList();

                // DENEME KAYITLARI DA BUDANMALI — burada yalnız ban'lar temizleniyordu ve
                // `Attempts` sonsuza kadar büyüyordu.
                //
                // Eşiğe hiç ulaşmayan IP'lerin kaydı hiç silinmiyordu: 200.000 farklı IP'den 3'er
                // deneme yapan dağıtık bir dalga hiçbir ban üretmez ama 200.000 kayıt bırakır.
                // Ondan sonra HER giriş denemesi (IsBannedAsync, girişin sıcak yolu) bu dosyanın
                // tamamını okuyup ayrıştırır, her başarısız deneme de tamamını yeniden yazar.
                // Yani korumanın kendisi bir DoS vektörüne dönüşüyordu.
                //
                // Pencere dışındaki kayıtlar zaten sayıma girmiyor (RegisterFailedAttemptAsync
                // penceredekilere bakar), dolayısıyla silmek hiçbir bilgi kaybettirmez.
                var window = TimeSpan.FromMinutes(Math.Max(1, state.WindowMinutes));
                var eskiDeneme = state.Attempts.RemoveAll(attempt => attempt.LastSeenUtc < now - window);

                if (expired.Count == 0 && eskiDeneme == 0)
                {
                    return;
                }

                state.Bans.RemoveAll(ban => expired.Contains(ban));
                await SaveAsync(state, cancellationToken);
            }, cancellationToken);

            foreach (var ban in expired)
            {
                await RemoveFirewallBlockAsync(ban.FirewallRuleName, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Brute-force engel temizliği sırasında hata.");
        }
    }

    /// <summary>
    /// Firewall engel kuralını yazar. Dönüş değeri KULLANILMALIDIR: kural yazılamadığında IP
    /// gerçekte engellenmemiştir ve panel "engellendi" demeye devam ederse yönetici korunduğunu sanır.
    /// </summary>
    private static async Task<bool> ApplyFirewallBlockAsync(BanRecord ban, CancellationToken cancellationToken)
    {
        var script = $"""
            $ErrorActionPreference = 'Stop'
            Remove-NetFirewallRule -DisplayName "{EscapePowerShellString(ban.FirewallRuleName)}" -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName "{EscapePowerShellString(ban.FirewallRuleName)}" -Direction Inbound -Action Block -RemoteAddress {ban.IpAddress} -Profile Any | Out-Null
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        return result.Success;
    }

    private static async Task RemoveFirewallBlockAsync(string ruleName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return;
        }

        var script = $"""
            Remove-NetFirewallRule -DisplayName "{EscapePowerShellString(ruleName)}" -ErrorAction SilentlyContinue | Out-Null
            """;

        await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
    }

    private async Task<BruteForceState> LoadAsync(CancellationToken cancellationToken)
    {
        return await fileStore.ReadAsync(StatePath, new BruteForceState(), cancellationToken);
    }

    private async Task SaveAsync(BruteForceState state, CancellationToken cancellationToken)
    {
        await fileStore.WriteAsync(StatePath, state, cancellationToken);
    }

    private static bool IsWhitelisted(BruteForceState state, string ip)
    {
        return state.WhitelistedIps.Any(entry => string.Equals(entry, ip, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyCollection<string> NormalizeWhitelist(IReadOnlyCollection<string> entries)
    {
        return entries
            .Select(Normalize)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .Distinct()
            .ToArray();
    }

    private static string? Normalize(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        var trimmed = ipAddress.Trim();
        // Loopback adreslerini engelleme — yerel yönetimi kilitleme riski.
        if (!IPAddress.TryParse(trimmed, out var parsed) || IPAddress.IsLoopback(parsed))
        {
            return null;
        }

        return parsed.ToString();
    }

    private static BruteForceStatusDto ToStatus(BruteForceState state)
    {
        return new BruteForceStatusDto
        {
            Settings = new BruteForceSettingsDto
            {
                Enabled = state.Enabled,
                MaxFailedAttempts = state.MaxFailedAttempts,
                WindowMinutes = state.WindowMinutes,
                BanMinutes = state.BanMinutes,
                WhitelistedIps = state.WhitelistedIps
            },
            BannedIps = state.Bans
                .OrderByDescending(ban => ban.BannedAtUtc)
                .Select(ban => new BannedIpDto
                {
                    IpAddress = ban.IpAddress,
                    BannedAtUtc = ban.BannedAtUtc,
                    ExpiresAtUtc = ban.ExpiresAtUtc,
                    FailedAttempts = ban.FailedAttempts,
                    FirewallRuleName = ban.FirewallRuleName
                })
                .ToArray()
        };
    }

    private static string EscapePowerShellString(string value) => value.Replace("\"", "`\"");

    private sealed class BruteForceState
    {
        public bool Enabled { get; set; } = true;
        public int MaxFailedAttempts { get; set; } = 10;
        public int WindowMinutes { get; set; } = 15;
        public int BanMinutes { get; set; } = 60;
        public IReadOnlyCollection<string> WhitelistedIps { get; set; } = [];
        public List<AttemptRecord> Attempts { get; set; } = [];
        public List<BanRecord> Bans { get; set; } = [];
    }

    private sealed class AttemptRecord
    {
        public string IpAddress { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int Count { get; set; }
    }

    private sealed class BanRecord
    {
        public string IpAddress { get; set; } = string.Empty;
        public DateTime BannedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public int FailedAttempts { get; set; }
        public string FirewallRuleName { get; set; } = string.Empty;
    }
}
