using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Services;

public sealed class TotpService : ITotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private static readonly TimeSpan TimeStep = TimeSpan.FromSeconds(30);

    public string GenerateSecret()
    {
        Span<byte> buffer = stackalloc byte[20];
        RandomNumberGenerator.Fill(buffer);
        return Base32Encode(buffer);
    }

    public string BuildProvisioningUri(string issuer, string accountName, string secret)
    {
        var escapedIssuer = Uri.EscapeDataString(issuer);
        var escapedAccountName = Uri.EscapeDataString(accountName);
        return $"otpauth://totp/{escapedIssuer}:{escapedAccountName}?secret={secret}&issuer={escapedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>
    /// Kodu doğrular; eşleşen zaman adımını döner. Adım, çağıranın kodu TÜKETEBİLMESİ için gerekli
    /// (tekrar oynatma koruması) — gerekçesi <see cref="ITotpService.VerifyCode"/>'da.
    /// </summary>
    public long? VerifyCode(string secret, string code)
    {
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != 6)
        {
            return null;
        }

        var secretBytes = Base32Decode(secret);
        var currentStep = GetCurrentTimeStepNumber();

        // BÜTÜN ADIMLAR HER ZAMAN DENENİR, ilk eşleşmede DÖNÜLMEZ: erken çıkış, "kaçıncı adımda
        // eşleşti" bilgisini çalışma SÜRESİNE yazardı. Kazanç küçük ama bedeli de yok — üç HMAC.
        long? eslesen = null;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = currentStep + offset;
            var expected = ComputeTotp(secretBytes, step);

            // Sabit zamanlı karşılaştırma: beklenen kod bir SIRDIR (gizli anahtardan türer) ve
            // string.Equals ilk farklı karakterde durur.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(normalizedCode)))
            {
                eslesen = step;
            }
        }

        return eslesen;
    }

    private static long GetCurrentTimeStepNumber()
    {
        return (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TimeStep.TotalSeconds);
    }

    private static string ComputeTotp(byte[] key, long timestepNumber)
    {
        Span<byte> timestepBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            timestepBytes[i] = (byte)(timestepNumber & 0xff);
            timestepNumber >>= 8;
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timestepBytes.ToArray());
        var offset = hash[^1] & 0x0f;

        var binaryCode = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        var otp = binaryCode % 1_000_000;
        return otp.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var output = new StringBuilder((int)Math.Ceiling(data.Length / 5d) * 8);
        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var currentByte in data)
        {
            bitBuffer = (bitBuffer << 8) | currentByte;
            bitCount += 8;

            while (bitCount >= 5)
            {
                var index = (bitBuffer >> (bitCount - 5)) & 0x1f;
                output.Append(Base32Alphabet[index]);
                bitCount -= 5;
            }
        }

        if (bitCount > 0)
        {
            var index = (bitBuffer << (5 - bitCount)) & 0x1f;
            output.Append(Base32Alphabet[index]);
        }

        return output.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        var sanitized = input.Trim().TrimEnd('=').ToUpperInvariant();
        if (sanitized.Length == 0)
        {
            return [];
        }

        var output = new List<byte>(sanitized.Length * 5 / 8);
        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var character in sanitized)
        {
            var index = Base32Alphabet.IndexOf(character);
            if (index < 0)
            {
                throw new InvalidOperationException("security.invalid_totp_secret");
            }

            bitBuffer = (bitBuffer << 5) | index;
            bitCount += 5;

            if (bitCount >= 8)
            {
                output.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xff));
                bitCount -= 8;
            }
        }

        return output.ToArray();
    }
}
