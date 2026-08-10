using System.Text.Json;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kuyruk ayrıştırıcısının GERÇEK <c>QueuedMessage</c> çıktısını doğru okuduğunu sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Neden ayrı bir test: kuyruk ekranının tamamı bu tek çeviriye bağlı. Bir alan adı yanlış
/// yazılırsa (bu depoda kota alanında tam olarak öyle oldu) ekran sessizce boş görünür —
/// istisna yok, günlük yok, yalnız "kuyruk boş" yalanı. Ayrıştırıcı saf bir fonksiyon olduğu
/// için canlı sunucu olmadan ölçülebilir.
/// </para>
/// <para>
/// Örnek gövde, Stalwart şema referansındaki <c>QueuedMessage</c> alanlarına göre kuruldu:
/// <c>createdAt</c>, <c>nextRetry</c>, <c>returnPath</c>, <c>size</c>, <c>receivedFromIp</c> ve
/// anahtarı e-posta adresi olan <c>recipients</c> HARİTASI (içinde <c>retryCount</c>,
/// <c>retryDue</c>, <c>queueName</c>, <c>status</c>).
/// </para>
/// </remarks>
public sealed class MailQueueParsingTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string GercekIleti = """
        {"id":"a1b2c3","createdAt":"2026-08-09T21:15:04Z","nextRetry":"2026-08-09T22:15:04Z",
         "returnPath":"gonderen@ornek.com","size":18342,"receivedFromIp":"203.0.113.9",
         "recipients":{
           "alici@uzak.com":{"retryCount":3,"retryDue":"2026-08-09T22:15:04Z","queueName":"remote",
                             "status":{"@type":"TemporaryFailure","reason":"connection timed out"}},
           "ikinci@uzak.com":{"retryCount":0,"retryDue":"2026-08-09T21:20:00Z","queueName":"remote",
                              "status":"Pending"}}}
        """;

    [Fact]
    public void Kuyruk_ayristirici_gercek_ciktidaki_zarf_bilgisini_okur()
    {
        var ileti = MailService.ParseQueueMessage(Parse(GercekIleti));

        Assert.Equal("a1b2c3", ileti.Id);
        Assert.Equal("gonderen@ornek.com", ileti.ReturnPath);
        Assert.Equal(18342L, ileti.SizeBytes);
        Assert.Equal("203.0.113.9", ileti.ReceivedFromIp);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 21, 15, 4, TimeSpan.Zero), ileti.CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 22, 15, 4, TimeSpan.Zero), ileti.NextRetryUtc);
    }

    /// <summary>
    /// Alıcılar HARİTADIR: anahtar adresin kendisidir. Diziymiş gibi okunursa "kime gidemedi"
    /// sorusu — kuyruk ekranının tek varlık sebebi — cevapsız kalır.
    /// </summary>
    [Fact]
    public void Kuyruk_ayristirici_alicilari_haritanin_ANAHTARINDAN_okur()
    {
        var ileti = MailService.ParseQueueMessage(Parse(GercekIleti));

        Assert.Equal(2, ileti.Recipients.Count);
        Assert.Contains(ileti.Recipients, item => item.Address == "alici@uzak.com");
        Assert.Contains(ileti.Recipients, item => item.Address == "ikinci@uzak.com");
    }

    [Fact]
    public void Kuyruk_ayristirici_alici_basina_deneme_ve_durumu_tasir()
    {
        var ileti = MailService.ParseQueueMessage(Parse(GercekIleti));
        var takilan = ileti.Recipients.Single(item => item.Address == "alici@uzak.com");

        Assert.Equal(3, takilan.RetryCount);
        Assert.Equal("remote", takilan.QueueName);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 22, 15, 4, TimeSpan.Zero), takilan.RetryDueUtc);

        // Durumun VARYANT ADI kısa alana, sunucunun söylediğinin TAMAMI ham alana gider.
        // Ham metin taşınmasaydı "sebebini oku" özelliği hiç çalışmadan var görünürdü.
        Assert.Equal("TemporaryFailure", takilan.Status);
        Assert.Contains("connection timed out", takilan.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>Durum düz metin olarak gelirse de okunur (varyant nesnesi tek biçim değildir).</summary>
    [Fact]
    public void Kuyruk_ayristirici_metin_durumu_da_okur()
    {
        var ileti = MailService.ParseQueueMessage(Parse(GercekIleti));
        var bekleyen = ileti.Recipients.Single(item => item.Address == "ikinci@uzak.com");

        Assert.Equal("Pending", bekleyen.Status);
    }

    /// <summary>
    /// Eksik/bozuk kayıt bütün listeyi düşürmemeli: tek bir tuhaf ileti yüzünden kuyruk ekranının
    /// tamamının boş kalması, ekranın olmamasıyla aynı kapıya çıkar.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"id":"x","recipients":null}""")]
    [InlineData("""{"id":"x","createdAt":"bozuk-tarih","size":"metin"}""")]
    [InlineData("""{"id":"x","recipients":{"a@b.c":{}}}""")]
    public void Kuyruk_ayristirici_eksik_alanlarda_patlamaz(string json)
    {
        var ileti = MailService.ParseQueueMessage(Parse(json));

        Assert.Equal("x", ileti.Id);
        Assert.Null(ileti.CreatedAtUtc);
        Assert.Equal(0L, ileti.SizeBytes);
    }

    /// <summary>
    /// Ayrıntı yanıtı kimliği taşımayabilir (nesne kendi id'siyle çağrılmıştır). O durumda
    /// kimlik kaybolursa "sil" düğmesi hangi iletiyi sileceğini bilemez.
    /// </summary>
    [Fact]
    public void Kuyruk_ayristirici_kimlik_gelmezse_verilen_kimligi_korur()
    {
        var ileti = MailService.ParseQueueMessage(Parse("""{"returnPath":"a@b.c"}"""), "dis-kimlik");

        Assert.Equal("dis-kimlik", ileti.Id);
    }
}
