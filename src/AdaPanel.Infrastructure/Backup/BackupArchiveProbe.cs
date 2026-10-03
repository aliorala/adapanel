using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// "Bu dosya GERÇEKTEN bir yedek mi?" ölçümünün TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN HATA:</b> yedek yolunda hiçbir yerde boyut ya da bütünlük sorulmuyordu.
/// <c>FinalizeBackupAsync</c> boyutu YAZIYOR ama DENETLEMİYORDU; <c>VerifyBackupsAsync</c> arşivi
/// HİÇ AÇMIYOR, yalnız dosyanın var olup olmadığına bakıyordu. İkisi birleşince panel sıfır
/// baytlık bir dosya için "yedek başarılı" ve arkasından "doğrulamadan geçti" diyebiliyordu.
/// </para>
/// <para>
/// <b>NE KADAR ÖLÇÜLÜR — ve neden bu kadar:</b> arşivi tam çıkarıp her girdiyi çözmek doğru
/// cevabı verirdi ama 40 GB'lık bir site yedeğinde gece işini saatlerce meşgul ederdi; pahalı
/// doğrulama, yapılmayan doğrulamaya dönüşür. Bunun yerine ZIP'in <b>merkezî dizini</b> okunur:
/// dosyanın sonundaki girdi tablosu (ad, sıkıştırılmış/açılmış boyut). Maliyeti girdi sayısıyla
/// orantılıdır, TEK BAYT bile açılmaz ve gerçek üç şeyi ölçer:
/// </para>
/// <list type="number">
///   <item><description>arşiv AÇILABİLİYOR mu (dizin okunabiliyor mu),</description></item>
///   <item><description>içinde girdi VAR mı,</description></item>
///   <item><description>o girdilerin açılmış toplam boyutu sıfırdan büyük mü.</description></item>
/// </list>
/// <para>
/// Ölçmediğimiz şey (her girdinin CRC'si, yani bit çürümesi) açıkça ölçülmemiştir ve
/// <see cref="BackupIntegrity.Sound"/> "bit bit doğrulandı" İDDİA ETMEZ — "boş/bozuk değil" der.
/// Bu ayrım yorumda durur ki bir sonraki okuyan, olmayan bir garantiye güvenmesin.
/// </para>
/// <para>
/// <b>ŞİFRELİ/SIKIŞTIRILMIŞ SARMALAYICI:</b> içi parola olmadan okunamaz. Bu durumda "bozuk"
/// DENMEZ (<see cref="BackupIntegrity.Unknown"/>): sağlam bir yedeği çöp ilan etmek, hiç
/// ölçmemekten kötüdür. Sıfır bayt ise sarmalayıcı olsun olmasın BOZUKTUR — boş dosya hiçbir
/// biçimde geçerli bir arşiv değildir.
/// </para>
/// </remarks>
internal static class BackupArchiveProbe
{
    /// <summary>Ölçüm sonucu: durum + ÇEVRİLMEYEN teknik ayrıntı (etiket-değer).</summary>
    /// <remarks>
    /// <paramref name="Detail"/> bu depoda çevrilmeyen alandır ve arayüz onu olduğu gibi basar;
    /// bu yüzden cümle değil, dilden bağımsız etiket-değer satırıdır (aynı karar
    /// <c>BackupService.ApplySkippedFiles</c> ve <c>VerifyBackupsAsync</c> içinde de yazılı).
    /// </remarks>
    internal readonly record struct Result(string State, string Detail)
    {
        /// <summary>Ölçüldü ve BOZUK bulundu (yalnız bu durumda kayıt "başarılı" sayılmaz).</summary>
        public bool Unsound => State == BackupIntegrity.Unsound;
    }

    /// <summary>
    /// Dosyayı ölçer. <paramref name="format"/> kaydın ANLAMSAL biçimidir ("zip"/"sql"/"bak");
    /// sarmalayıcılar dosyanın KENDİ BAŞLIĞINDAN tanınır, bu etiketten değil (aynı kural
    /// <c>OpenContentAsync</c>'te de geçerli).
    /// </summary>
    public static Result Measure(string path, string format)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Result(BackupIntegrity.Unknown, "path=empty; probe=skipped");
        }

        // Historical restore records may point to an extraction directory instead of
        // the archive. No archive can be measured here; current restores preserve Path
        // and publish their destination separately as RestoredPath.
        if (Directory.Exists(path))
        {
            return new Result(BackupIntegrity.Unknown, "target=directory; probe=skipped");
        }

        long uzunluk;
        try
        {
            var bilgi = new FileInfo(path);
            if (!bilgi.Exists)
            {
                // Dosyanın YOKLUĞU ayrı bir gerçektir ve zaten ayrı ölçülüp raporlanıyor
                // (LocalMissing / backup.verify.missing_local). Burada ikinci kez, daha az
                // bilgiyle söylemek gürültü olurdu.
                return new Result(BackupIntegrity.Unknown, "file=missing; probe=skipped");
            }

            uzunluk = bilgi.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Result(BackupIntegrity.Unknown, $"stat=failed; reason={exception.GetType().Name}");
        }

        // SIFIR BAYT: tartışmasız. Hiçbir biçimde (zip, sql, şifreli, sıkıştırılmış) geçerli
        // olamaz ve bu, "yedek başarılı" denen en tehlikeli dosyadır.
        if (uzunluk == 0)
        {
            return new Result(BackupIntegrity.Unsound, "bytes=0; archive=empty");
        }

        // Sarmalayıcı varsa içi okunamaz: ölçemediğimizi SÖYLERİZ, uydurmayız.
        if (BackupArchiveCipher.IsEncryptedFile(path) || BackupDumpCompression.IsCompressedFile(path))
        {
            return new Result(BackupIntegrity.Unknown, $"wrapper=present; bytes={uzunluk}; deep_probe=skipped");
        }

        // ZIP DIŞI BİÇİMLER (sql/bak dökümleri): açılabilirlik ancak veritabanı motoruyla
        // ölçülebilirdi; yedek alırken bir motoru meşgul etmek doğrulamanın bedelini yedeğin
        // kendisinden büyük yapardı. Ölçülen tek gerçek boyuttur ve sıfır olmadığı yukarıda
        // görüldü.
        if (!string.Equals(format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            return new Result(BackupIntegrity.Sound, $"format={format}; bytes={uzunluk}; deep_probe=not_applicable");
        }

        try
        {
            using var arsiv = ZipFile.OpenRead(path);
            var girdi = arsiv.Entries.Count;
            long acilmis = 0;
            foreach (var kayit in arsiv.Entries)
            {
                acilmis += kayit.Length;
            }

            if (girdi == 0)
            {
                return new Result(BackupIntegrity.Unsound, $"bytes={uzunluk}; entries=0");
            }

            // AÇILMIŞ TOPLAM BOYUT RAPORLANIR AMA TEK BAŞINA "BOZUK" SAYILMAZ.
            //
            // ÖLÇÜLDÜ: "girdi var ama toplam açılmış boyut 0" ilk yazımda bozuk sayılıyordu ve
            // mevcut bir test hemen kızardı — çünkü tek girdisi BOŞ BİR DOSYA olan arşiv
            // (ör. sitenin tek içeriği sıfır baytlık bir index.php) YAPISAL OLARAK GEÇERLİDİR ve
            // geri yüklendiğinde o dosya aynen geri gelir. Onu "bozuk" damgalamak, sağlam bir
            // yedeği çöp ilan etmek olurdu — bu ölçümün var oluş sebebinin tam tersi.
            //
            // Aradığımız "anlamsız yedek" hâli zaten daha keskin iki ölçümle yakalanıyor: dosya
            // sıfır bayt ya da arşivde HİÇ girdi yok. Sayı yine de ayrıntıda durur; teşhis için
            // değerlidir ("30 GB'lık site 12 girdi, 4 KB" bir insana çok şey söyler).
            return new Result(BackupIntegrity.Sound, $"bytes={uzunluk}; entries={girdi}; expanded_bytes={acilmis}");
        }
        catch (InvalidDataException exception)
        {
            // Merkezî dizin okunamadı: dosya zip DEĞİL ya da yarım yazılmış. Bu, ölçülebilen bir
            // BOZUKLUKTUR — "ölçemedim" değil.
            return new Result(BackupIntegrity.Unsound, $"bytes={uzunluk}; archive=unreadable; reason={exception.GetType().Name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Dosya o an kilitli/erişilemez: ÖLÇEMEDİK. Kilitli dosyaya "bozuk" demek, çalışan bir
            // yedeği kullanıcıya çöp diye göstermek olurdu.
            return new Result(BackupIntegrity.Unknown, $"bytes={uzunluk}; open=failed; reason={exception.GetType().Name}");
        }
    }
}
