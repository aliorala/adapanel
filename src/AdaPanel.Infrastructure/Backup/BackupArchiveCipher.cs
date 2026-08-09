using System.Buffers.Binary;
using System.Security.Cryptography;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedek dosyalarının AES-256-GCM ile şifrelenmesi/çözülmesi. Panelin ŞİFRELEME BİÇİMİ BURADADIR;
/// başka hiçbir yerde şifreleme yapılmaz (tek kaynak).
/// <para>
/// <b>Neden gerekli:</b> yedekler FTP/S3/WebDAV gibi ÜÇÜNCÜ TARAF depolara yükleniyor. Şifresiz bir
/// arşiv, müşterinin veritabanı dökümünü ve postasını o depoya okuyabilen herkese açar (KVKK/GDPR).
/// </para>
/// <para>
/// <b>Kripto icat edilmez:</b> .NET'in <see cref="AesGcm"/> ve <see cref="Rfc2898DeriveBytes"/>
/// sınıfları kullanılır. Anahtar paroladan PBKDF2-SHA256 ile ve KAYIT BAŞINA RASTGELE tuzla
/// türetilir; tuz/tur sayısı/nonce öneki başlıkta AÇIK taşınır (bunlar sır değildir, parola sırdır).
/// </para>
/// <para>
/// <b>Nonce asla tekrar kullanılmaz:</b> her dosyanın anahtarı rastgele tuzdan türediği için
/// dosyaya özeldir; o anahtar altında her blok için sayaç bir artar. Son blok nonce'un son
/// baytıyla işaretlenir → arşivin SONUNDAN kesilmesi (truncation) de yakalanır.
/// </para>
/// <para>
/// <b>Bütünlük:</b> her bloğun GCM etiketi doğrulanmadan o bloğun düz metni ASLA yazılmaz; ayrıca
/// başlık, her bloğun ilişkili verisidir (AAD) → başlık kurcalanırsa çözme başarısız olur.
/// </para>
/// <para>
/// <b>PAROLA KAYBOLURSA YEDEK GERİ GETİRİLEMEZ.</b> Kurtarma kapısı, yedek anahtar ya da panelin
/// saklayabildiği bir "ana anahtar" YOKTUR — olsaydı şifrelemenin koruduğu şey de olmazdı.
/// </para>
/// </summary>
public static class BackupArchiveCipher
{
    /// <summary>Şifreli yedek dosyasının uzantısı (ör. <c>site-20260808.zip.enc</c>).</summary>
    public const string EncryptedExtension = ".enc";

    /// <summary>Dosya başlığındaki biçim imzası. Biçim değişirse buradaki sürüm numarası artar.</summary>
    private static readonly byte[] Magic = "ADAPBKE1"u8.ToArray();

    /// <summary>
    /// PBKDF2 tur sayısı. Ürün kararı: en az 600.000 (OWASP 2023 PBKDF2-SHA256 önerisi).
    /// Dosyanın başlığında yazılı olduğu için ileride artırılabilir; ESKİ dosyalar kendi
    /// tur sayısıyla çözülmeye devam eder.
    /// </summary>
    public const int Pbkdf2Iterations = 600_000;

    private const int MinimumIterations = 100_000;
    private const int SaltSize = 32;
    private const int KeySize = 32;   // AES-256
    private const int TagSize = 16;   // GCM etiketi
    private const int NonceSize = 12; // GCM için önerilen 96 bit
    private const int NoncePrefixSize = 7;
    private const int HeaderSize = 56;

    /// <summary>Blok başına DÜZ METİN boyutu. Bellekte tutulan en büyük parça budur (1 MiB).</summary>
    private const int ChunkSize = 1 << 20;

    private const int MaxChunkSize = 1 << 26; // bozuk/kötücül başlığın 4 GiB tampon ayırtmasını engeller

    /// <summary>
    /// Dosya AdaPanel şifreli yedeği mi? <b>Karar dosyanın BAŞLIĞINDAN verilir</b>, uzantıdan ya da
    /// panel kaydından değil: eski şifresiz yedekler (ve elle kopyalanmış dosyalar) çalışmaya devam
    /// etmeli, kaydı kaybolmuş bir dosya da doğru tanınmalı.
    /// </summary>
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[8];
            return stream.Length >= HeaderSize + TagSize
                   && stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head.SequenceEqual(Magic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Okuyamadıysak "şifresiz" DEMEYİZ; çağıran zaten dosyayı açamayacak ve gerçek
            // hatayı görecek. Burada yanlış "şifresiz" cevabı vermek, arşivi düz zip sanıp
            // "bozuk dosya" hatası üretirdi.
            return false;
        }
    }

    /// <summary>
    /// <paramref name="sourcePath"/> dosyasını şifreleyip <paramref name="targetPath"/> yazar.
    /// Kaynak dosyaya DOKUNMAZ (silme kararı çağıranındır).
    /// </summary>
    public static async Task EncryptFileAsync(
        string sourcePath,
        string targetPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            // Boş parola = şifreleme yok demektir; sessizce "şifrelendi" denmesi en tehlikeli sonuçtur.
            throw new AppException("backup.encryption_password_required", 400, 400);
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var header = BuildHeader(Pbkdf2Iterations, ChunkSize, salt, noncePrefix);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            await target.WriteAsync(header, cancellationToken);

            // 0 numaralı blok: DÜZ METNİ BOŞ olan "parola doğrulayıcı". Tek işi, başlığı türetilen
            // anahtarla mühürlemek. Çözerken bu etiket tutmazsa PAROLA YANLIŞTIR; tuttuktan sonra
            // bir veri bloğu tutmazsa DOSYA BOZUK/KURCALANMIŞTIR. Bu ayrım olmadan kullanıcıya her
            // iki durumda da "bozuk dosya" denirdi — parolasını yanlış yazdığını asla anlamazdı.
            var headerTag = new byte[TagSize];
            aes.Encrypt(BuildNonce(noncePrefix, 0, isFinal: false), ReadOnlySpan<byte>.Empty, Span<byte>.Empty, headerTag, header);
            await target.WriteAsync(headerTag, cancellationToken);

            var plainBuffer = new byte[ChunkSize];
            var cipherBuffer = new byte[ChunkSize];
            var tag = new byte[TagSize];
            uint counter = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAtLeastAsync(plainBuffer, plainBuffer.Length, throwOnEndOfStream: false, cancellationToken);
                var isFinal = read < plainBuffer.Length;

                aes.Encrypt(
                    BuildNonce(noncePrefix, counter, isFinal),
                    plainBuffer.AsSpan(0, read),
                    cipherBuffer.AsSpan(0, read),
                    tag,
                    header);

                await target.WriteAsync(cipherBuffer.AsMemory(0, read), cancellationToken);
                await target.WriteAsync(tag, cancellationToken);

                if (isFinal)
                {
                    break;
                }

                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Şifreli yedeği çözüp <paramref name="targetPath"/> yazar. Etiketi tutmayan hiçbir blok
    /// yazılmaz; hata durumunda yarım kalan hedef dosya SİLİNİR (yarım düz metin kimseye lazım değil).
    /// </summary>
    /// <exception cref="AppException">
    /// <c>backup.decrypt_wrong_password</c> — parola yanlış (ya da başlık kurcalanmış);
    /// <c>backup.decrypt_corrupt</c> — arşiv bozuk/kurcalanmış (GCM etiketi tutmuyor);
    /// <c>backup.not_encrypted</c> — dosya AdaPanel şifreli yedeği değil.
    /// </exception>
    public static async Task DecryptFileAsync(
        string sourcePath,
        string targetPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new AppException("backup.encryption_password_required", 400, 400);
        }

        var basarili = false;
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[HeaderSize];
            if (await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) != header.Length ||
                !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new AppException("backup.not_encrypted", 400, 400);
            }

            var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
            if (iterations < MinimumIterations || chunkSize <= 0 || chunkSize > MaxChunkSize)
            {
                // Başlık AAD olarak doğrulanıyor ama bu değerler doğrulamadan ÖNCE kullanılıyor
                // (anahtar türetme + tampon ayırma). Sınır denetimi olmadan bozuk bir başlık
                // paneli dakikalarca oyalayabilir ya da devasa tampon ayırtabilirdi.
                throw new AppException("backup.decrypt_corrupt", 400, 400, "Basliktaki parametreler gecersiz.");
            }

            var salt = header.AsSpan(16, SaltSize).ToArray();
            var noncePrefix = header.AsSpan(48, NoncePrefixSize).ToArray();
            var key = DeriveKey(password, salt, iterations);

            try
            {
                using var aes = new AesGcm(key, TagSize);

                // 1) PAROLA DOĞRULAMASI: başlık etiketi. Buradan geçemezsek tek bir bayt bile
                //    çözmeye kalkışmadan "parola yanlış" deriz.
                var headerTag = new byte[TagSize];
                if (await source.ReadAtLeastAsync(headerTag, TagSize, throwOnEndOfStream: false, cancellationToken) != TagSize)
                {
                    throw new AppException("backup.decrypt_corrupt", 400, 400, "Baslik etiketi eksik.");
                }

                try
                {
                    aes.Decrypt(BuildNonce(noncePrefix, 0, isFinal: false), ReadOnlySpan<byte>.Empty, headerTag, Span<byte>.Empty, header);
                }
                catch (CryptographicException)
                {
                    throw new AppException("backup.decrypt_wrong_password", 400, 400);
                }

                // 2) VERİ BLOKLARI. Buradan sonraki her etiket hatası "bozuk/kurcalanmış" demektir:
                //    anahtarın doğru olduğunu 1. adımda ÖLÇTÜK.
                await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var cipherBuffer = new byte[chunkSize];
                var plainBuffer = new byte[chunkSize];
                var tag = new byte[TagSize];
                uint counter = 1;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Kalan bayt sayısı son bloğu belirler: son blokta blok boyutundan az veri olur.
                    // Tam blok + etiket okuduktan sonra dosya bitiyorsa o da son bloktur.
                    var remaining = source.Length - source.Position;
                    if (remaining < TagSize)
                    {
                        throw new AppException("backup.decrypt_corrupt", 400, 400, "Arsiv beklenenden once bitti (kesilmis olabilir).");
                    }

                    var payload = (int)Math.Min(remaining - TagSize, chunkSize);
                    if (await source.ReadAtLeastAsync(cipherBuffer.AsMemory(0, payload), payload, throwOnEndOfStream: false, cancellationToken) != payload ||
                        await source.ReadAtLeastAsync(tag, TagSize, throwOnEndOfStream: false, cancellationToken) != TagSize)
                    {
                        throw new AppException("backup.decrypt_corrupt", 400, 400, "Arsiv beklenenden once bitti (kesilmis olabilir).");
                    }

                    var isFinal = source.Position >= source.Length;
                    try
                    {
                        aes.Decrypt(
                            BuildNonce(noncePrefix, counter, isFinal),
                            cipherBuffer.AsSpan(0, payload),
                            tag,
                            plainBuffer.AsSpan(0, payload),
                            header);
                    }
                    catch (CryptographicException)
                    {
                        // Etiket tutmadı → düz metin YAZILMAZ. Kesilmiş arşiv de buraya düşer:
                        // son blok işareti nonce'a girdiği için "sondan kesme" saldırısı da yakalanır.
                        throw new AppException("backup.decrypt_corrupt", 400, 400, $"Blok {counter} dogrulanamadi.");
                    }

                    await target.WriteAsync(plainBuffer.AsMemory(0, payload), cancellationToken);

                    if (isFinal)
                    {
                        break;
                    }

                    counter++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            basarili = true;
        }
        finally
        {
            if (!basarili)
            {
                TryDelete(targetPath);
            }
        }
    }

    private static byte[] BuildHeader(int iterations, int chunkSize, byte[] salt, byte[] noncePrefix)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), chunkSize);
        salt.CopyTo(header, 16);
        noncePrefix.CopyTo(header, 48);
        // header[55] ayrılmış (0) — biçim değişikliği için yer.
        return header;
    }

    /// <summary>
    /// Nonce = 7 bayt dosyaya özel rastgele önek + 4 bayt blok sayacı + 1 bayt "son blok" bayrağı.
    /// Anahtar zaten dosyaya özel (rastgele tuz) olduğundan sayaç tek başına tekrarsızlığı garanti
    /// eder; rastgele önek fazladan güvenlik payıdır.
    /// </summary>
    private static byte[] BuildNonce(byte[] prefix, uint counter, bool isFinal)
    {
        var nonce = new byte[NonceSize];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize, 4), counter);
        nonce[NonceSize - 1] = isFinal ? (byte)1 : (byte)0;
        return nonce;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temizlik best-effort; asıl hata çağırana zaten gidiyor.
        }
    }
}
