using System.Runtime.Versioning;
using AdaPanel.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Gönderen sahteciliği kuralını panel her AÇILDIĞINDA Stalwart'a uygular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bir açılış işi ve neden kurulum adımı DEĞİL:</b> kural
/// <c>ComponentBootstrapper.ConfigureStalwartListenersAsync</c> içine konsaydı yalnız Stalwart'ı
/// BU sürümle KURAN makinelerde çalışırdı — yani açığı asıl taşıyan MEVCUT kurulumlara hiç
/// ulaşmazdı. Güncelleme paneli yeniden başlattığı için açılış, mevcut kurulumların düzeltmeyi
/// aldığı tek güvenilir andır.
/// </para>
/// <para>
/// <b>Neden tek atış ve tekrar yok:</b> yazılan şey bir yapılandırma yuvasıdır, bir bakım turu
/// değil. Kural yerindeyse <see cref="MailService.SyncSenderPolicyAsync"/> hiçbir şey yazmaz
/// (idempotent). Stalwart o an ayakta değilse iş sessizce ölmez: uyarı loglanır ve bir sonraki
/// açılışta yeniden denenir. Ayrıca alan adı oluşturmanın ardından da en iyi gayretle çağrılır,
/// böylece "Stalwart'ı yeni kurdum" durumunda kural yeniden başlatmayı beklemez.
/// </para>
/// <para>
/// <b>Neden açılışta bekliyor:</b> Stalwart panelle aynı makinede bir zamanlanmış görev olarak
/// başlar; yönetim ucu (8080) panelden birkaç saniye sonra hazır olur. Beklemeden sorsaydık her
/// açılışta gereksiz bir "ulaşılamadı" uyarısı üretirdik.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MailSenderPolicyWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MailSenderPolicyWorker> logger) : BackgroundService
{
    /// <summary>Stalwart yönetim ucunun hazır olması için açılış payı.</summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var mail = scope.ServiceProvider.GetRequiredService<MailService>();
            var result = await mail.SyncSenderPolicyAsync(stoppingToken);

            switch (result)
            {
                case MailService.SenderPolicySyncResult.Applied:
                    logger.LogInformation(
                        "Mail sender policy applied: unauthenticated sessions can no longer use a locally hosted sender domain.");
                    break;
                case MailService.SenderPolicySyncResult.WrittenNotActivated:
                    // "Uygulandı" DENMEZ: ayar diskte, sunucu hâlâ eski kuralla çalışıyor.
                    logger.LogWarning(
                        "Mail sender policy was WRITTEN but the mail server did not reload it (MtaStageMail.isSenderAllowed). "
                        + "Unauthenticated senders can still forge locally hosted sender domains until the mail service is restarted.");
                    break;
                case MailService.SenderPolicySyncResult.AlreadyApplied:
                    logger.LogDebug("Mail sender policy already in place; nothing written.");
                    break;
                case MailService.SenderPolicySyncResult.ForeignExpression:
                case MailService.SenderPolicySyncResult.Unavailable:
                    // SyncSenderPolicyAsync bu iki durumu zaten gerekçesiyle loglar.
                    break;
            }

            // From başlığı hizalaması DATA aşaması script'ine yazılır; aynı açılışta o da hizalanır.
            await mail.SyncDataStageScriptForSenderPolicyAsync(stoppingToken);
        }
        catch (AppException exception) when (exception.MessageKey is "mail.not_installed" or "mail.not_configured")
        {
            // Bu makinede Stalwart yok/yapılandırılmamış — beklenen ve gürültü yapmaması gereken durum.
            logger.LogDebug("Mail sender policy skipped: {Reason}.", exception.MessageKey);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // SESSİZ YUTMA YOK: kural uygulanmadıysa sunucu hâlâ açıktır, bu görülmeli.
            logger.LogWarning(
                exception,
                "Mail sender policy could not be applied; unauthenticated sender forgery may still be possible. "
                + "It will be retried on the next panel start.");
        }
    }
}
