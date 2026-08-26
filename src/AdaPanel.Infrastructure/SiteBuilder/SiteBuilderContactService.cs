using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>Formun sonucu + kaydın konusunu çözmek için gereken site adı.</summary>
/// <remarks>
/// Site adı geri döner, çünkü denetim kaydının KİRACI BAĞINI (<c>SubscriptionUserId</c>) yalnız
/// site adından çözebiliyoruz ve uç kimliksiz olduğu için kaydın kiracının görünümüne düşmesinin
/// TEK yolu konusudur. Adı döndürmeseydik, site sahibi kendi formuna gelen gönderimleri kendi
/// denetim ekranında hiç göremezdi.
/// </remarks>
public sealed record SiteBuilderContactResult(SiteBuilderContactOutcome Outcome, string SiteName);

/// <summary>Kimliksiz iletişim formu gönderimini işleyen servis.</summary>
public interface ISiteBuilderContactService
{
    Task<SiteBuilderContactResult> SubmitAsync(
        string siteKey,
        SiteBuilderContactSubmission submission,
        string? visitorIp,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// İletişim formunun sunucu tarafı: doğrula → hız sınırı → GERÇEKTEN gönder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu uç kimliksizdir ve E-POSTA GÖNDERİR</b> — yani yanlış yazılırsa bir spam silahıdır.
/// Sırayla kapatılan şeyler:
/// </para>
/// <list type="number">
/// <item>
/// <b>Açık röle değil:</b> alıcı adresi ziyaretçiden ALINMAZ; sitenin sahibinin kaydettiği
/// tasarımdan (iletişim bölümünün e-posta alanından) okunur. Ziyaretçinin adresi yalnız GÖVDEYE
/// yazılır, hiçbir başlık alanına girmez.
/// </item>
/// <item>
/// <b>Başlık enjeksiyonu yok:</b> başlığa giren her metin
/// <see cref="SiteBuilderContactForm.HeaderSafe"/>'ten geçer.
/// </item>
/// <item>
/// <b>Hız sınırı + gönderen bekleme + bal küpü:</b>
/// <see cref="SiteBuilderContactRateLimiter"/>. Dış servise (CAPTCHA) bağımlılık YOK.
/// </item>
/// <item>
/// <b>Sadece formu AÇMIŞ siteler:</b> tasarımda form kapalıysa uç, sanki hiç yokmuş gibi
/// davranır. Yani panelde hiç kullanılmayan bir özellik, sunucuda açık bir yüzey bırakmaz.
/// </item>
/// </list>
/// <para>
/// <b>IIS'e HİÇ sorulmaz.</b> Tasarım dosyası site anahtarıyla doğrudan okunur. Bu bilinçli: uç
/// kimliksiz ve IIS'teki bütün siteleri gezen bir çözüm, tek satırlık bir istekle sunucuya iş
/// yaptıran bir kaldıraç olurdu. Aynı gerekçe <c>MailListPublicController</c>'da ölçülmüş ve
/// yazılıydı.
/// </para>
/// <para>
/// <b>Sıralama ÖNEMLİ:</b> önce ucuz denetimler (anahtar biçimi, tasarım okuma, form açık mı),
/// sonra doğrulama, en sonda hız sınırı ve gönderim. Hız sınırı sayacı ancak GERÇEK bir gönderim
/// adayı için harcanır; biçimsiz istekler sayacı tüketip meşru ziyaretçiyi dışarıda bırakmaz.
/// </para>
/// </remarks>
public sealed class SiteBuilderContactService(
    JsonFileStore fileStore,
    INotificationService notificationService,
    SiteBuilderContactRateLimiter rateLimiter,
    ILogger<SiteBuilderContactService> logger) : ISiteBuilderContactService
{
    public async Task<SiteBuilderContactResult> SubmitAsync(
        string siteKey,
        SiteBuilderContactSubmission submission,
        string? visitorIp,
        CancellationToken cancellationToken = default)
    {
        var key = (siteKey ?? string.Empty).Trim();
        if (!SiteBuilderContactEndpoint.IsValidSiteKey(key))
        {
            return new SiteBuilderContactResult(SiteBuilderContactOutcome.NotAvailable, string.Empty);
        }

        var project = await fileStore.ReadAsync<SiteBuilderProjectDto?>(
            SiteBuilderService.ProjectPathForKey(key),
            null,
            cancellationToken);

        var recipient = RecipientOf(project);
        if (project is null || recipient.Length == 0)
        {
            // "Form kapalı" ile "site yok" AYNI cevabı verir: farklı cevaplar, hangi site
            // anahtarlarının gerçek olduğunu taramaya yarayan bir sinyal olurdu.
            return new SiteBuilderContactResult(SiteBuilderContactOutcome.NotAvailable, project?.SiteName ?? string.Empty);
        }

        var siteName = (project.SiteName ?? string.Empty).Trim();

        var validation = SiteBuilderContactForm.Validate(submission);
        if (validation.Value is null)
        {
            if (validation.Outcome == SiteBuilderContactOutcome.Trapped)
            {
                // Bal küpü kayda değer: sunucuda bir kötüye kullanım denemesi olduğunu gösteren
                // tek iz bu. Ziyaretçinin yazdığı metin GÜNLÜĞE de geçmez — günlük, korumaya
                // çalıştığımız verinin ikinci kopyası olmamalı.
                logger.LogWarning(
                    "Site kurucu iletisim formu bal kupune takildi: {Site} ({Ip})",
                    siteName,
                    visitorIp ?? "-");
            }

            return new SiteBuilderContactResult(validation.Outcome, siteName);
        }

        if (!rateLimiter.TryAcquire(visitorIp, key, validation.Value.Email, DateTime.UtcNow))
        {
            logger.LogWarning(
                "Site kurucu iletisim formu hiz sinirina takildi: {Site} ({Ip})",
                siteName,
                visitorIp ?? "-");
            return new SiteBuilderContactResult(SiteBuilderContactOutcome.RateLimited, siteName);
        }

        try
        {
            await notificationService.SendEmailToAsync(
                recipient,
                SiteBuilderContactForm.BuildSubject(siteName, validation.Value),
                SiteBuilderContactForm.BuildBody(siteName, validation.Value, visitorIp),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // GÖNDERİLEMEDİ, ve bu ziyaretçiye SÖYLENİR. Bu özelliğin ilk turda kapsam dışı
            // bırakılma gerekçesi tam olarak buydu: hata yutulup "gönderildi" denseydi, ziyaretçi
            // gelmeyecek bir yanıtı beklerdi. Sebep (SMTP ayarsız, sunucu reddetti) günlüğe
            // yazılır; ziyaretçiye sunucunun iç durumu anlatılmaz.
            logger.LogError(
                exception,
                "Site kurucu iletisim formu gonderilemedi: {Site}",
                siteName);
            return new SiteBuilderContactResult(SiteBuilderContactOutcome.SendFailed, siteName);
        }

        return new SiteBuilderContactResult(SiteBuilderContactOutcome.Sent, siteName);
    }

    /// <summary>
    /// Formun alıcısı: iletişim bölümü AÇIK, formu AÇIK ve adresi geçerliyse o adres; yoksa boş.
    /// </summary>
    /// <remarks>
    /// Üç koşul da burada, tek yerde ölçülüyor ve <c>SiteBuilderRenderer.AppendContactForm</c>
    /// aynı üç koşulu ölçüyor. İki taraf ayrı ayrı karar verdiği için ikisinin de tutarlı olması
    /// gerekir; testte "form basılıyorsa uç kabul eder, basılmıyorsa uç reddeder" olarak
    /// sabitlendi — aksi hâlde ya çalışmayan bir form basılır ya da çalışan bir uç gizli kalırdı.
    /// </remarks>
    private static string RecipientOf(SiteBuilderProjectDto? project)
    {
        var contact = (project?.Sections ?? [])
            .FirstOrDefault(section => string.Equals(section.Type, "contact", StringComparison.OrdinalIgnoreCase));

        if (contact is null || !contact.Enabled || !contact.ContactFormEnabled)
        {
            return string.Empty;
        }

        var email = (contact.Email ?? string.Empty).Trim();
        return SiteBuilderContactForm.IsUsableAddress(email) ? email : string.Empty;
    }
}
