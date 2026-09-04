namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Veritabanı yönetici hesabı sertleştirmesinin (mysql_secure_installation eşdeğeri) SONUCU.
///
/// <para>
/// <b>NEDEN LİSTE YETMİYOR.</b> Eskiden bu işlem yalnız <c>IReadOnlyList&lt;string&gt;</c>
/// döndürüyordu: KALDIRILABİLEN hesapların adları. Kaldırılamayanlar hiçbir yere yazılmıyordu,
/// çünkü <c>DROP USER</c> hataları <c>catch (MySqlException) { }</c> ile yutuluyordu. Sonuç,
/// denetim kaydında şu cümleye dönüşüyordu:
/// </para>
///
/// <code>
/// removed.Count == 0 ? "Hardening: no insecure admin accounts found." : ...
/// </code>
///
/// <para>
/// Yani üç güvensiz hesap BULUNUP üçünün de silinmesi başarısız olduğunda panel "güvensiz
/// yönetici hesabı bulunamadı" diyordu — kodun bir ifade önce SELECT ile saydığı hesaplar
/// hakkında. Bu, panelin bilmediğini değil, BİLDİĞİNİN TERSİNİ söylemesidir. Bu depoda
/// <c>root@'%'</c> parolasız kalması bilinen bir kritik açık; onu kapatmak için düğmeye basan
/// yönetici "temiz" yazısını görüp başka yere bakar.
/// </para>
///
/// <para>
/// <b>ÜÇ DEĞERLİ.</b> Kaldırıldı / kaldırılamadı / doğrulanamadı ayrı ayrı taşınır ve
/// başarısızlar ADIYLA yazılır. "Üçü başarısız" demek yöneticiye HANGİSİ olduğunu söylemez.
/// </para>
/// </summary>
public sealed class DatabaseHardeningReport
{
    /// <summary>Kaldırıldığı DOĞRULANAN güvensiz hesaplar / uygulanan sertleştirmeler.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];

    /// <summary>
    /// Güvensiz olduğu ÖLÇÜLEN ama kaldırılamayan hesaplar — her biri ADIYLA ve sebebiyle.
    /// Boş değilse sunucu HÂLÂ güvensizdir.
    /// </summary>
    public IReadOnlyList<string> Failed { get; init; } = [];

    /// <summary>
    /// İşlem yapıldı ama sonucu geri okunamadı. "Başarısız" DEĞİLDİR; "bilmiyoruz" demektir.
    /// </summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>
    /// Sertleştirmeden SONRA güvensiz hesap listesi yeniden okunabildi mi.
    /// <para>
    /// <b>Varsayılan false</b> (fail-safe): raporu dolduran biri bu alanı unutursa panel
    /// "doğrulandı" demiş olmaz.
    /// </para>
    /// </summary>
    public bool Measured { get; init; }

    /// <summary>
    /// Sunucunun güvensiz hesaplardan arındığı SÖYLENEBİLİR mi.
    /// <para>
    /// Yalnız geri okuma YAPILDIYSA ve geriye ne başarısız ne doğrulanamayan kaldıysa
    /// <c>true</c>'dur. Ölçüm yapılmadıysa cevap "temiz" değil, "bilinmiyor"dur.
    /// </para>
    /// </summary>
    public bool? Clean => !Measured
        ? null
        : Failed.Count == 0 && Unverified.Count == 0;

    /// <summary>Hiçbir şey yapılmadı ve hiçbir şey ölçülmedi (motor desteklemiyor vb.).</summary>
    public static DatabaseHardeningReport Bos() => new();

    /// <summary>
    /// İki raporu birleştirir. Panel aynı işlemi birden çok profil için (ör. MariaDB ve MySQL)
    /// koşturabiliyor; sonuçların TEK bir cümleye indirilmesi tek yerde olmalı, yoksa iki çağıran
    /// sessizce ayrı kararlar verir.
    /// </summary>
    public static DatabaseHardeningReport Birlestir(DatabaseHardeningReport ilk, DatabaseHardeningReport ikinci)
        => new()
        {
            Applied = [.. ilk.Applied, .. ikinci.Applied],
            Failed = [.. ilk.Failed, .. ikinci.Failed],
            Unverified = [.. ilk.Unverified, .. ikinci.Unverified],

            // Ölçüm İKİSİNDE de yapılmışsa ölçülmüş sayılır. Biri ölçülmediyse toplam sonuç
            // hakkında "temiz" diyemeyiz — yarısına hiç bakmadık.
            Measured = ilk.Measured && ikinci.Measured
        };
}
