using AdaPanel.Core.Dtos.Ssl;

namespace AdaPanel.Infrastructure.Ssl;

/// <summary>Sertifika deposundan yapılan ölçümün SONUCU.</summary>
internal enum SslStoreProbeOutcome
{
    /// <summary>
    /// ÖLÇÜM YAPILAMADI (depo açılamadı, host adı boş). Kayda DOKUNULMAZ.
    /// </summary>
    Unmeasured = 0,

    /// <summary>Depo OKUNDU; bu host için sertifika YOK.</summary>
    NotFound = 1,

    /// <summary>Depo OKUNDU; sertifika bulundu (parmak izi/bitiş/kaynak ölçülmüş değerlerdir).</summary>
    Found = 2
}

/// <summary>Tek bir SSL kaydının gerçekle karşılaştırılmasından çıkan ölçüm.</summary>
internal sealed record SslStoreProbe(SslStoreProbeOutcome Outcome, string Thumbprint, DateTime NotAfterUtc, string Source)
{
    public static SslStoreProbe Unmeasured() => new(SslStoreProbeOutcome.Unmeasured, string.Empty, default, string.Empty);

    public static SslStoreProbe NotFound() => new(SslStoreProbeOutcome.NotFound, string.Empty, default, string.Empty);

    public static SslStoreProbe Found(string thumbprint, DateTime notAfterUtc, string source) =>
        new(SslStoreProbeOutcome.Found, thumbprint ?? string.Empty, notAfterUtc, source ?? string.Empty);
}

/// <summary>Uzlaştırmanın özeti — çağıran hem ne olduğunu bilir hem de yazıp yazmayacağına karar verir.</summary>
internal sealed record SslReconcileResult(bool ShouldPersist, int Verified, int MissingFromStore, int Unmeasured);

/// <summary>
/// <c>config/ssl.json</c> kaydını sertifika deposundaki GERÇEKLE uzlaştırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı ve saf bir sınıf:</b> buradaki kural üç satırlık bir <c>if</c> değil, bir
/// DAVRANIŞ SÖZLEŞMESİDİR ve panelin en gürültülü yanlış alarmını (her gün "sertifikanızın süresi
/// doldu" e-postası) üreten yer tam olarak burasıydı. Sözleşme sertifika deposundan, IIS'ten ve
/// dosya sisteminden bağımsız olarak test edilebilmeli; aksi hâlde ancak canlı bir sunucuda,
/// 90 gün sonra fark edilirdi.
/// </para>
/// <para>
/// <b>Sözleşme:</b>
/// <list type="number">
/// <item>Ölçüm YAPILAMADIYSA kayda DOKUNULMAZ. "Depoyu okuyamadım" ile "sertifika yok" aynı şey
/// değildir; ikisini karıştırmak geçici bir arızayı kalıcı bir yanlış duruma çevirir.</item>
/// <item>Ölçüm sertifikayı BULDUYSA kayıt gerçeğe göre TAZELENİR (parmak izi, bitiş tarihi,
/// kaynak). win-acme yenilemesi sertifikayı aynı konu adıyla ama YENİ parmak iziyle bırakır;
/// eski kod bunu hiç görmediği için kayıt yenilenmiş sertifikada bile "süresi doldu" diyordu.</item>
/// <item>Ölçüm sertifikayı BULAMADIYSA kayıt SİLİNMEZ — <see cref="SslCertificateDto.IsPresentInStore"/>
/// false yazılır. Silmek, "hiç sertifika yoktu" demek olurdu; oysa doğrusu "kayıt var, karşılığı
/// yok" bilgisini yüzeye çıkarmaktır.</item>
/// </list>
/// </para>
/// </remarks>
internal static class SslStateReconciler
{
    /// <summary>
    /// Doğrulama zaman damgasını diske yazma sıklığı.
    /// <para>
    /// Ölçüm her okuma yolunda (SSL ekranı her açıldığında) yapılır; her seferinde dosyaya yazmak
    /// gereksiz disk trafiği ve eşzamanlı yazımlarla yarış demek olurdu. Gerçek DEĞİŞTİĞİNDE
    /// zaten koşulsuz yazılır; bu eşik yalnızca "hiçbir şey değişmedi, sadece saat ilerledi"
    /// hâli içindir.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan VerificationStampInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Kayıtları ölçümle uzlaştırır. Kayıt nesneleri YERİNDE güncellenir (çağıran hem belleğe hem
    /// gerekiyorsa diske aynı sonucu yansıtır).
    /// </summary>
    /// <param name="records">Uzlaştırılacak kayıtlar (ssl.json içeriği).</param>
    /// <param name="probe">Kayıt başına ölçüm. Ölçemediyse <see cref="SslStoreProbe.Unmeasured"/> dönmelidir.</param>
    /// <param name="nowUtc">Doğrulama zaman damgası.</param>
    public static SslReconcileResult Reconcile(
        IEnumerable<SslCertificateDto> records,
        Func<SslCertificateDto, SslStoreProbe> probe,
        DateTime nowUtc)
    {
        var yazilmali = false;
        var dogrulanan = 0;
        var depodaYok = 0;
        var olculemeyen = 0;

        foreach (var kayit in records)
        {
            var olcum = probe(kayit) ?? SslStoreProbe.Unmeasured();

            if (olcum.Outcome == SslStoreProbeOutcome.Unmeasured)
            {
                // ÖLÇEMEDİK: kayda DOKUNMA. Ne "yok" de, ne de doğrulama tarihini tazele —
                // tazelemek, hiç yapılmamış bir ölçümü yapılmış gibi göstermek olurdu.
                olculemeyen++;
                continue;
            }

            if (olcum.Outcome == SslStoreProbeOutcome.NotFound)
            {
                depodaYok++;
                yazilmali |= kayit.IsPresentInStore != false;
                kayit.IsPresentInStore = false;

                // NotAfterUtc'ye DOKUNULMAZ: sertifika depoda yok, yeni bir bitiş tarihi de yok.
                // Uydurulmuş bir tarih yazmak, "yok" bilgisini gizlerdi.
            }
            else
            {
                dogrulanan++;

                if (!string.Equals(kayit.Thumbprint, olcum.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    // Parmak izi değişmiş = sertifika YENİLENMİŞ. Panelin bunu görmediği yıllarda
                    // yenilenen her sertifika kayıtta eski tarihiyle kalıyordu.
                    kayit.Thumbprint = olcum.Thumbprint;
                    yazilmali = true;
                }

                if (kayit.NotAfterUtc != olcum.NotAfterUtc)
                {
                    kayit.NotAfterUtc = olcum.NotAfterUtc;
                    yazilmali = true;
                }

                if (!string.IsNullOrEmpty(olcum.Source) &&
                    !string.Equals(kayit.Source, olcum.Source, StringComparison.Ordinal))
                {
                    kayit.Source = olcum.Source;
                    yazilmali = true;
                }

                yazilmali |= kayit.IsPresentInStore != true;
                kayit.IsPresentInStore = true;
            }

            // Buraya yalnızca GERÇEKTEN ölçülen kayıtlar ulaşır; damga o yüzden burada basılır.
            yazilmali |= kayit.LastVerifiedUtc is null
                         || nowUtc - kayit.LastVerifiedUtc.Value >= VerificationStampInterval;
            kayit.LastVerifiedUtc = nowUtc;
        }

        return new SslReconcileResult(yazilmali, dogrulanan, depodaYok, olculemeyen);
    }

    /// <summary>
    /// Kayıt kimliği (site + host + port). Ölçüm dosya kilidinin DIŞINDA yapıldığı için, kilit
    /// altında yeniden okunan listeye ölçümler bu anahtarla eşlenir.
    /// </summary>
    public static string Key(SslCertificateDto record) =>
        $"{record.SiteName.Length}:{record.SiteName.ToLowerInvariant()}|{record.HostName.Length}:{record.HostName.ToLowerInvariant()}|{record.Port}";
}
