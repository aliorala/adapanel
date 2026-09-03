namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Askı/askı kaldırma denetim kaydının site cümlesi — TEK KAYNAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen sınıf:</b> siteler tek tek durdurulur/başlatılır ve tek bir sitenin hatası
/// <c>catch</c> ile yutulup devam edilir — doğrusu da budur, bir site yüzünden askı yarım
/// kalmamalı. Ama denetim kaydı koşulsuz İSTENEN sayıyı yazarsa, beş siteden üçü durdurulamasa
/// bile kayıt "5 site(s) stopped" der. Ödeme yapmayan müşterinin siteleri yayında kalır ve kayıt
/// aksini söyler; parasını ödemişinki kapalı kalır ve kayıt açıldığını söyler.
/// </para>
/// <para>
/// <b>Neden ayrı bir sınıf:</b> aynı cümle önce <c>SubscriptionEnforcementService</c>'in iki
/// yolunda elle yazılmıştı; yöneticinin müşteriyi ELLE askıya aldığı üçüncü yol
/// (<c>UserManagementService.ApplySuspensionStateAsync</c>) ise eski hâlde, İSTENEN sayıyı
/// yazmaya devam ediyordu. Bu depoda defalarca ölçülen ikinci kalıp tam olarak budur: koruma bir
/// yola konur, kardeşi sessizce eski hâlde kalır. Cümleyi üçüncü kez kopyalamak, dördüncü yolun
/// da aynı şekilde geride kalmasını garanti ederdi.
/// </para>
/// <para>
/// <b>BAŞARISIZ SİTELER ADIYLA yazılır.</b> "3 tanesi durdurulamadı" demek yöneticiye HANGİ
/// sitenin hâlâ yayında olduğunu söylemez — kayıt okunabilir ama işe yaramaz olurdu.
/// </para>
/// <para>
/// <b>Her şey yolundayken cümle SADE kalır.</b> Her kayda uyarı iliştirmek, her başarılı askıyı
/// şüpheli gösterirdi; sürekli uyaran bir kayıt, uyarmayan kadar kullanışsızdır.
/// </para>
/// </remarks>
internal static class SiteSuspensionReport
{
    /// <summary>Durdurma cümlesi. Durdurulamayanlar hâlâ YAYINDADIR.</summary>
    internal static string Stopped(int requested, IReadOnlyCollection<string> failed) =>
        Describe(requested, failed, "stopped", "STILL RUNNING");

    /// <summary>Başlatma cümlesi. Başlatılamayanlar hâlâ KAPALIDIR.</summary>
    internal static string Started(int requested, IReadOnlyCollection<string> failed) =>
        Describe(requested, failed, "started", "STILL STOPPED");

    private static string Describe(int requested, IReadOnlyCollection<string> failed, string verb, string warning)
    {
        // YAPILAN sayı, İSTENENDEN başarısızlar düşülerek bulunur. Ayrı bir sayaç tutmak, sayacın
        // listeyle zamanla ayrışabileceği ikinci bir gerçek kaynağı yaratırdı.
        var done = requested - failed.Count;

        return failed.Count == 0
            ? $"{done} site(s) {verb}."
            : $"{done} of {requested} site(s) {verb}; {warning}: {string.Join(", ", failed)}.";
    }
}
