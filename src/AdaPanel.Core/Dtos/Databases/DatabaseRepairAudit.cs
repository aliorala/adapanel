namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Tablo onarımının denetim kaydı cümlesi — İSTENENDEN değil, ÖLÇÜLEN sonuçtan üretilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>DatabasesController.CheckAndRepairAsync</c> kayda koşulsuz
/// <c>"Tables repaired: " + string.Join(", ", repairTables)</c> yazıyordu — yani KULLANICININ
/// SEÇTİĞİ adları. Oysa <see cref="DatabaseCheckResultDto"/> onarımdan SONRA yeniden yapılan
/// denetimi tablo tablo taşıyor ve gerçek cevap orada duruyordu.
/// </para>
/// <para>
/// İstenen ad ile yapılan iş dört ayrı şekilde ayrışır ve dördü de sessizdir:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Tablo veritabanında YOK.</b> Envanterde bulunmayan bir ad için hiçbir ifade çalışmaz —
/// döngü o adı hiç görmez. Kayıt yine de "onarıldı" diyordu.
/// </description></item>
/// <item><description>
/// <b>Motor onarımı DESTEKLEMİYOR.</b> <see cref="DatabaseCheckResultDto"/> açıklamasının
/// bir numaralı kuralı: InnoDB'de <c>REPAIR TABLE</c> hata vermez, hiçbir şey yapmaz ve
/// <c>status: OK</c> döner. Panel bu yüzden motoru önceden okuyup ifadeyi hiç göndermez —
/// ama kayıt o tabloyu da "onarıldı" listesine yazıyordu.
/// </description></item>
/// <item><description>
/// <b>InnoDB YENİDEN İNŞA edildi.</b> <c>OPTIMIZE TABLE</c> tabloyu gerçekten yeniden yazar;
/// bu faydalıdır ama BİR ONARIM DEĞİLDİR ve DTO'nun kendi açıklaması da öyle diyor. İki işi
/// aynı kelimeyle kaydetmek, bozuk bir InnoDB tablosunun onarıldığı izlenimini verirdi.
/// </description></item>
/// <item><description>
/// <b>Onarım ÇALIŞTI ama tablo HÂLÂ BOZUK.</b> Onarımdan sonraki denetim <c>error</c> döndüğünde
/// bile kayıt başarı yazıyordu. Bedeli en ağır olan durum budur: yönetici "onardım" kaydına
/// güvenip elindeki yedeği siler.
/// </description></item>
/// </list>
/// <para>
/// <b>BAŞARISIZLAR ADIYLA yazılır.</b> "2 tanesi onarılamadı" demek yöneticiye HANGİ tablonun
/// hâlâ bozuk olduğunu söylemez — kayıt okunabilir ama işe yaramaz olurdu.
/// </para>
/// <para>
/// <b>Her şey yolundayken cümle SADE kalır.</b> Her kayda uyarı iliştirmek, her başarılı onarımı
/// şüpheli gösterirdi; sürekli uyaran bir kayıt, uyarmayan kadar kullanışsızdır.
/// </para>
/// </remarks>
public static class DatabaseRepairAudit
{
    /// <summary>
    /// Onarım denetim kaydının ayrıntı cümlesi.
    /// </summary>
    /// <param name="result">Onarımdan SONRA yeniden yapılan denetimin sonucu.</param>
    /// <param name="requested">Kullanıcının onarılmasını istediği tablo adları.</param>
    public static string Describe(DatabaseCheckResultDto result, IReadOnlyCollection<string> requested)
    {
        var istenen = new List<string>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ad in requested ?? [])
        {
            if (!string.IsNullOrWhiteSpace(ad) && gorulen.Add(ad.Trim()))
            {
                istenen.Add(ad.Trim());
            }
        }

        if (istenen.Count == 0)
        {
            return "No table was selected for repair.";
        }

        var tablolar = result.Tables.ToDictionary(item => item.Table, item => item, StringComparer.OrdinalIgnoreCase);

        var onarilan = new List<string>();
        var yenidenInsa = new List<string>();
        var halaBozuk = new List<string>();
        var denenmeyen = new List<string>();
        var bulunamayan = new List<string>();

        foreach (var ad in istenen)
        {
            if (!tablolar.TryGetValue(ad, out var tablo))
            {
                // Envanterde YOK: hiçbir ifade çalışmadı. "Onarıldı" demek tamamen uydurma olurdu.
                bulunamayan.Add(ad);
                continue;
            }

            if (!tablo.CanRepair && !tablo.CanRebuild)
            {
                // Motor ne onarımı ne yeniden inşayı destekliyor: panel ifadeyi hiç göndermedi.
                denenmeyen.Add(ad);
                continue;
            }

            if (!string.Equals(tablo.Status, "healthy", StringComparison.Ordinal))
            {
                halaBozuk.Add(ad);
                continue;
            }

            // OPTIMIZE bir onarım DEĞİLDİR; ayrı sayılır ki kayıt iki farklı işi aynı kelimeyle
            // anlatmasın.
            (tablo.CanRepair ? onarilan : yenidenInsa).Add(ad);
        }

        var sorunVar = halaBozuk.Count > 0 || denenmeyen.Count > 0 || bulunamayan.Count > 0;

        if (!sorunVar && yenidenInsa.Count == 0)
        {
            return $"{onarilan.Count} table(s) repaired: {string.Join(", ", onarilan)}.";
        }

        var bolumler = new List<string> { $"repair requested for {istenen.Count} table(s)" };

        if (onarilan.Count > 0)
        {
            bolumler.Add($"repaired: {string.Join(", ", onarilan)}");
        }

        if (yenidenInsa.Count > 0)
        {
            bolumler.Add($"rebuilt (NOT a repair): {string.Join(", ", yenidenInsa)}");
        }

        if (halaBozuk.Count > 0)
        {
            bolumler.Add($"STILL FAULTY: {string.Join(", ", halaBozuk)}");
        }

        if (denenmeyen.Count > 0)
        {
            bolumler.Add($"NOT ATTEMPTED (engine supports neither repair nor rebuild): {string.Join(", ", denenmeyen)}");
        }

        if (bulunamayan.Count > 0)
        {
            bolumler.Add($"NOT FOUND in database: {string.Join(", ", bulunamayan)}");
        }

        return string.Join("; ", bolumler) + ".";
    }
}
