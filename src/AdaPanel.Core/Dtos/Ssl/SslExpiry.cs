namespace AdaPanel.Core.Dtos.Ssl;

/// <summary>
/// Sertifika bitis siniflandirmasi -- TEK KAYNAK. "valid" ile "yarin bitiyor" ayni etiketi
/// tasimamali: 1 gunu kalmis bir sertifika panelde yesil/gecerli gorunuyordu.
/// </summary>
public static class SslExpiry
{
    public const string Valid = "valid";
    public const string RenewalDue = "renewal_due";
    public const string Expiring = "expiring";
    public const string Expired = "expired";

    /// <summary>Bu gun sayisinin altinda yenileme zamani gelmis demektir (win-acme de ~30 gunde yeniler).</summary>
    public const int RenewalDueDays = 30;

    /// <summary>Bu gun sayisinin altinda sertifika acilen bir arizaya cok yakindir.</summary>
    public const int ExpiringDays = 7;

    public static string Classify(DateTime notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc == default) return Valid;
        var left = notAfterUtc - nowUtc;
        if (left <= TimeSpan.Zero) return Expired;
        if (left <= TimeSpan.FromDays(ExpiringDays)) return Expiring;
        if (left <= TimeSpan.FromDays(RenewalDueDays)) return RenewalDue;
        return Valid;
    }
}
