using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IFileService
{
    Task<FileWorkspaceDto> GetWorkspaceAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Authorized file publication is not implemented.");

    Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir ya da birden çok ögeyi başka klasöre TAŞIR; tek öge + <c>NewName</c> ile YENİDEN ADLANDIRIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden vardı olmalı:</b> panelde taşıma/yeniden adlandırma hiç yoktu — kullanıcı bir dosyayı
    /// komşu klasöre almak için indirip tekrar yüklemek zorundaydı (büyük dosyada pratikte imkânsız,
    /// klasörde ise hiç mümkün değil).
    /// </para>
    /// <para>
    /// <b>Sözleşme (uygulamanın uyması ZORUNLU olan kısım):</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Kaynak ve hedef sanal alanın içinde olmalıdır; dışarı çıkan istek REDDEDİLİR (istisna).</item>
    /// <item>Yol üzerindeki hiçbir bağlantı (junction/symlink) TAKİP EDİLMEZ — bir bağlantıyı taşımak
    /// ya da üzerine yazmak, hedefindeki müşteri sitesini yok edebilir.</item>
    /// <item>Hedefte aynı adlı öge varsa VARSAYILAN davranış REDDETMEKTİR; üzerine yazma yalnız
    /// <c>Overwrite</c> ile ve yalnız DOSYALAR için mümkündür.</item>
    /// <item>Bir klasör KENDİ ALT AĞACINA taşınamaz.</item>
    /// <item>Kısmi başarısızlık istisna değil, öge başına RAPOR üretir.</item>
    /// </list>
    /// </remarks>
    Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir ya da birden çok ögeyi başka klasöre KOPYALAR; kaynak YERİNDE KALIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var olmalı:</b> panelde kopyalama hiç yoktu — kullanıcı bir dosyayı yedeklemek ya da
    /// bir klasörü "denemeden önce" çoğaltmak için indirip tekrar yüklemek zorundaydı; klasörde bu
    /// pratikte hiç mümkün değildi. Oysa kopyalama, taşımanın aksine GERİ ALINABİLİR bir işlemdir ve
    /// kullanıcıların riskli bir değişiklikten önce başvurduğu ilk korumadır: yokluğu, insanları
    /// doğrudan taşımaya (yani veri kaybı riskine) itiyordu.
    /// </para>
    /// <para>
    /// <b>Sözleşme (uygulamanın uyması ZORUNLU olan kısım) — güvenlik kısmı
    /// <see cref="MoveAsync"/> ile BİREBİR AYNIDIR:</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Kaynak ve hedef sanal alanın içinde olmalıdır; dışarı çıkan istek REDDEDİLİR (istisna).</item>
    /// <item>Yol üzerindeki hiçbir bağlantı (junction/symlink) TAKİP EDİLMEZ. Kopyalamada bu kural
    /// ÖZYİNELEME İÇİNDE DE geçerlidir: bir bağlantıyı özyinelemeli kopyalamak ya sonsuz döngü ya da
    /// sanal alan DIŞINDAKİ veriyi kiracının klasörüne çekmek demektir. Atlanan bağlantı rapor
    /// satırında bildirilir (<c>files.copy_link_skipped</c>), sessizce geçilmez.</item>
    /// <item>Hedefte aynı adlı öge varsa VARSAYILAN davranış REDDETMEKTİR; üzerine yazma yalnız
    /// <c>Overwrite</c> ile ve yalnız DOSYALAR için mümkündür. Var olan bir KLASÖR <c>Overwrite</c>
    /// ile bile EZİLMEZ — ezmek, hedefin özyinelemeli silinmesi demektir.</item>
    /// <item>Bir klasör KENDİ ALT AĞACINA kopyalanamaz: kopya, kopyalanmakta olan ağacın içinde
    /// büyümeye devam ederdi (sonsuz büyüme, disk dolana kadar).</item>
    /// <item>Kısmi başarısızlık istisna değil, öge başına RAPOR üretir.</item>
    /// </list>
    /// <para>
    /// <b>Taşımadan tek farkı ÖGE BAŞINA yapılan iştir</b> (kopyala vs. yer değiştir); istek biçimini
    /// doğrulayan güvenlik ön hazırlığı iki akışta da TEK bir yardımcıdan gelir, çünkü ikisinden
    /// birinde unutulan bir denetim tek başına deliktir.
    /// </para>
    /// </remarks>
    Task<CopyFilesResultDto> CopyAsync(CopyFilesRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> SetPermissionsAsync(SetFilePermissionsRequest request, CancellationToken cancellationToken = default);

    Task<string> SaveUploadAsync(string path, Stream stream, CancellationToken cancellationToken = default);

    Task<string> SaveUploadAsync(string path, Stream stream,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Authorized file publication is not implemented.");

    /// <summary>
    /// İndirilecek dosyanın FİZİKSEL YOLUNU döndürür — içeriğini değil.
    /// <para>
    /// Önceden <c>byte[]</c> dönüyordu, yani dosya TAMAMEN belleğe alınıyordu ve boyut sınırı
    /// yoktu. Kiracının FTP/git ile koyduğu 3 GB'lık bir dosyayı indirmek tek bir dizi tahsisi
    /// demekti (2 GB üstü .NET'te zaten atılır) → OutOfMemoryException → panel süreci TÜM
    /// kiracılar için çöker. Yükleme 200 MB ile sınırlıydı, indirmede hiçbir sınır yoktu.
    /// </para>
    /// <para>
    /// Yol döndürmek, çağıranın dosyayı akış hâlinde vermesini sağlar (<c>PhysicalFile</c>) —
    /// yedek indirme akışında zaten kullanılan ve kanıtlanmış yöntem.
    /// </para>
    /// </summary>
    Task<(string FileName, string ContentType, string PhysicalPath)> DownloadAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Authorized file publication is not implemented.");

    Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FileEntryDto>> SearchAsync(SearchFilesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir klasörün ÖZYİNELEMELİ boyutunu ve öge sayılarını hesaplar (Plesk'teki "Calculate size").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var olmalı:</b> listede klasörlerin boyutu 0 görünür (bu doğrudur, boyut ancak
    /// yürünerek bulunur) — yani panelde "diski hangi klasör yiyor" sorusunun cevabı HİÇ yoktu.
    /// Disk dolduğunda kullanıcının tek çaresi sunucuya RDP ile girmekti; kotası dolan bir kiracının
    /// ise hiçbir çaresi yoktu.
    /// </para>
    /// <para>
    /// <b>Sözleşme (uygulamanın uyması ZORUNLU olan kısım):</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Yol sanal alanın içinde olmalıdır; dışarı çıkan istek REDDEDİLİR (istisna). Yol
    /// çözümlemesi diğer uçlarla AYNI kapıdan geçer — ayrı bir yol birleştirme yazılmaz.</item>
    /// <item>Bağlantılar (junction/symlink) TAKİP EDİLMEZ: bir bağlantıyı takip etmek ya sanal alan
    /// DIŞINI toplama katmak (kiracıya komşusunun/panelin disk kullanımını ölçtürmek) ya da kendi
    /// üst ağacını gösteren bir bağlantıda SONSUZ döngü demektir.</item>
    /// <item>Hesap BÜTÇELİDİR (öge sayısı + süre). Bütçe dolduğunda istisna atılmaz, o ana kadarki
    /// toplam döner ve <see cref="DirectorySizeDto.Truncated"/> ile SÖYLENİR — kısmi bir sonucu tam
    /// sonuç gibi döndürmek, kullanıcının disk kararını yanlış sayıya dayandırması demektir.</item>
    /// <item>Okunamayan (erişimi reddedilen) alt klasör hesabı ÇÖKERTMEZ; atlanır ama yine
    /// <see cref="DirectorySizeDto.Truncated"/> ile bildirilir.</item>
    /// <item><see cref="CancellationToken"/> onurlandırılır: kullanıcı sekmeyi kapattığında yürüyüş
    /// sürmez.</item>
    /// </list>
    /// </remarks>
    Task<DirectorySizeDto> CalculateSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verilen sanal yolların diskte kapladığı TOPLAM — disk KOTASI denetiminin ön ölçümü.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <see cref="CalculateSizeAsync"/> yetmiyor:</b> o uç TEK bir KLASÖR içindir ve bir
    /// DOSYA yolu verildiğinde bilerek <c>files.not_found</c> atar. Kota denetiminin sorduğu soru
    /// farklı: "kullanıcının seçtiği şu 8 ögenin (klasör + dosya karışık, bazısı belki hiç yok)
    /// toplamı ne?" Bu farkı çağıranın kendi döngüsüyle kapatması, aynı ağacın panelde İKİNCİ bir
    /// kuralla gezilmesi demek olurdu — bu depoda o hata beş kez ısırdı.
    /// </para>
    /// <para>
    /// <b>Sözleşme:</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Her yol sanal alan kapısından geçer; dışarı çıkan yol REDDEDİLİR (istisna). Ölçüm bir
    /// okumadır, ama kapsam dışı bir klasörün BOYUTUNU söylemek de sızıntıdır.</item>
    /// <item>DİSKTE OLMAYAN yol istisna DEĞİL, 0 sayılır: yeni bir dosya yazılırken hedef henüz
    /// yoktur ve kopyalamada olmayan kaynak zaten öge başına raporlanır.</item>
    /// <item>Bütün yollar TEK bir bütçeyi paylaşır; bütçe dolarsa toplam bir ALT SINIRDIR ve
    /// <see cref="DirectorySizeDto.Truncated"/> bunu SÖYLER. Kota bir güvenlik sınırı olduğu için
    /// çağıran bu bayrağı yok SAYAMAZ.</item>
    /// <item>Bağlantılar (junction/symlink) sayılmaz — kopyalama da onları kopyalamaz, yani ölçüm
    /// ile yapılacak iş aynı ağacı görür.</item>
    /// </list>
    /// </remarks>
    Task<DirectorySizeDto> MeasureAsync(string[] paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir zip arşivinin AÇILDIĞINDA kaplayacağı toplam bayt (sıkıştırılmamış).
    /// </summary>
    /// <remarks>
    /// Kota denetimi sıkıştırılmış boyuta bakamaz: 1 MB'lık bir arşiv 10 GB açabilir ("zip bombası")
    /// ve o fark tam olarak korunması gereken şeydir. Arşiv geçerli değilse
    /// <c>files.invalid_archive</c> ile reddedilir — <c>ExtractAsync</c> ile AYNI kural, çünkü iki
    /// yol farklı şeye "geçerli arşiv" derse kota, açılmayacak bir dosyayı ölçer.
    /// </remarks>
    Task<long> MeasureArchiveContentBytesAsync(string path, CancellationToken cancellationToken = default);
}
