using System.Runtime.Versioning;
using AdaPanel.Infrastructure.Support;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>Kararlı makine parmak izini üreten taraf.</summary>
public interface IMachineFingerprintProvider
{
    /// <summary>64 karakterlik onaltılık parmak izi. Aynı makinede AYNI değeri döner.</summary>
    string Get();

    /// <summary>Parmak izinin hangi kaynaklardan türetildiği (destek teşhisi için).</summary>
    string GetSource();
}

/// <summary>
/// Windows'ta KARARLI makine kimliğini okur ve parmak izini üretir.
/// </summary>
/// <remarks>
/// <para><b>Neden MAC adresi DEĞİL.</b> Bir barındırma sunucusunda ağ arayüzü kümesi sürekli
/// değişir: bu panelin KENDİSİ Docker Engine kurunca sanal anahtar/NAT arayüzleri doğar
/// (<c>ComponentBootstrapper.InstallDockerEngineAsync</c>), Hyper-V rolü sanal anahtar ekler, VPN
/// istemcisi TAP arayüzü ekler, NIC teaming arayüzleri birleştirir. Enumerasyon sırası da garanti
/// değildir. MAC'e dayanan bir parmak izi bu olayların HERHANGİ BİRİNDE değişir ve müşterinin
/// lisansı "başka makineye taşınmış" görünürdü. Aynı gerekçeyle disk seri numarası da (disk
/// değişimi, RAID/sanal disk taşıma) kullanılmaz.</para>
///
/// <para><b>Neden Windows MachineGuid.</b>
/// <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> Windows kurulumu sırasında bir kez
/// üretilir; yeniden başlatma, donanım değişikliği, NIC ekleme/çıkarma, sürücü güncellemesi, alan
/// adına katılma ve makine adı değişikliği onu DEĞİŞTİRMEZ. Yalnız işletim sistemi yeniden kurulunca
/// (ya da sysprep ile) değişir — bu zaten "yeni kurulum"dur ve lisansın yeniden aktive edilmesi
/// doğru davranıştır. <b>Bu makinede ölçüldü:</b> okunabiliyor ve tek bir GUID döndürüyor.</para>
///
/// <para><b>Neden ayrıca bir kurulum kimliği.</b> Sysprep'lenmemiş bir görüntüden çoğaltılan
/// sunucularda MachineGuid AYNI kalabilir (barındırmada sık görülen senaryo). Panelin kendi ürettiği
/// kurulum kimliği veri kökünde saklanır ve iki kurulumu ayırır.</para>
///
/// <para><b>Neden SMBIOS/BIOS değerleri HİÇ kullanılmıyor.</b> Bu makinede ölçüldü:
/// <c>Win32_BIOS.SerialNumber</c> = "To Be Filled By O.E.M" (tüm aynı model makinelerde AYNI),
/// <c>Win32_ComputerSystemProduct.UUID</c> = <c>1273EE80-F197-3B91-0000-000000000000</c> (yarısı
/// doldurulmamış). Üstelik sanal makine bir konaktan diğerine taşınınca SMBIOS UUID değişebilir:
/// girdiye eklenseydi parmak izi canlı göç sırasında sessizce kayardı. Girdi sayısını azaltmak
/// burada doğrudan kararlılık demektir.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MachineFingerprintProvider : IMachineFingerprintProvider
{
    private const string CryptographyKeyPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValueName = "MachineGuid";

    /// <summary>Kurulum kimliği dosyası (veri köküne göre). Düz metin, tek satır.</summary>
    internal const string InstallIdRelativePath = "config/license/install-id";

    private readonly JsonFileStore fileStore;
    private readonly Lazy<(string Fingerprint, string Source)> cached;

    public MachineFingerprintProvider(JsonFileStore fileStore)
    {
        this.fileStore = fileStore;
        // Lazy: parmak izi süreç ömrü boyunca DEĞİŞMEZ; her çağrıda kayıt defteri/disk okumak hem
        // gereksiz hem de "aynı istek içinde iki farklı değer" riski taşır.
        cached = new Lazy<(string, string)>(Compute, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Get() => cached.Value.Fingerprint;

    public string GetSource() => cached.Value.Source;

    private (string Fingerprint, string Source) Compute()
    {
        var installId = ReadOrCreateInstallId();
        var machineGuid = ReadMachineGuid();

        if (MachineFingerprint.IsUsableIdentity(machineGuid))
        {
            return (MachineFingerprint.Compute(machineGuid, installId), "machineguid+installid");
        }

        // Kayıt defteri okunamadı: kurulum kimliği TEK BAŞINA da kararlıdır (veri kökünde durur).
        // Makineye değil kuruluma bağlanmış olur; bu, hiç parmak izi üretememekten iyidir.
        return (MachineFingerprint.Compute(MachineFingerprint.Missing, installId), "installid-only");
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            // 64-bit görünüm AÇIKÇA seçilir: panel 32-bit bir süreçten çalıştırılırsa WOW64
            // yönlendirmesi farklı bir alt anahtara düşürebilir ve parmak izi yalnız süreç
            // mimarisi değiştiği için "makine değişti" gibi kayardı.
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(CryptographyKeyPath);
            return key?.GetValue(MachineGuidValueName) as string;
        }
        catch
        {
            // Kısıtlı hesap / sertleştirilmiş sunucu: teşhis değil kararlılık peşindeyiz.
            return null;
        }
    }

    /// <summary>
    /// Kurulum kimliğini okur; yoksa BİR KEZ üretip yazar.
    /// </summary>
    /// <remarks>
    /// Dosya yazılamıyorsa (salt okunur veri kökü) her seferinde YENİ bir GUID üretmek yerine sabit
    /// yer tutucu döneriz. Her açılışta değişen bir parmak izi, olmayan parmak izinden kötüdür:
    /// müşteri her yeniden başlatmada "lisans başka makinede" hatası alırdı.
    /// </remarks>
    private string ReadOrCreateInstallId()
    {
        try
        {
            var path = fileStore.ResolvePath(InstallIdRelativePath);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var created = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return MachineFingerprint.Missing;
        }
    }
}
