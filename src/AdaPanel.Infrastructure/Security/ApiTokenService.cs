using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Security;

/// <inheritdoc />
internal sealed class ApiTokenService(AdaPanelDbContext dbContext) : IApiTokenService
{
    /// <summary>Anahtarın önekindeki etiket; günlüklerde/sızıntı taramalarında tanınabilir olsun diye.</summary>
    private const string TokenPrefix = "adap_";

    /// <summary>256 bit entropi. Kısa anahtar, kaba kuvvete açık kapı bırakır.</summary>
    private const int TokenByteLength = 32;

    public async Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tokens = await dbContext.ApiTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId)
            .OrderByDescending(token => token.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return tokens.Select(ToDto).ToList();
    }

    public async Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            throw new AppException("apitoken.invalid_name", 400, 400);
        }

        // Kriptografik rastgele; tahmin edilebilir bir üreteç (Guid/Random) burada kabul edilemez.
        var raw = RandomNumberGenerator.GetBytes(TokenByteLength);
        var value = TokenPrefix + Convert.ToHexString(raw).ToLowerInvariant();

        var entity = new PanelApiToken
        {
            UserId = userId,
            Name = name,
            TokenHash = ComputeHash(value),
            Suffix = value[^4..],
            AllowDestructive = request.AllowDestructive,
            AllowTerminal = request.AllowTerminal,
            ExpiresAtUtc = request.ExpiresInDays > 0
                ? DateTime.UtcNow.AddDays(request.ExpiresInDays)
                : null
        };

        dbContext.ApiTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Düz metin değer YALNIZCA burada döner; bir daha üretilemez.
        return new CreatedApiTokenDto { Token = ToDto(entity), Value = value };
    }

    public async Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ApiTokens
            .FirstOrDefaultAsync(token => token.Id == tokenId && token.UserId == userId, cancellationToken)
            ?? throw new AppException("apitoken.not_found", 404, 404);

        if (entity.RevokedAtUtc is not null)
        {
            return;
        }

        entity.RevokedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken) || !presentedToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Karma üzerinden aranır: düz metin hiçbir zaman veritabanında bulunmaz, dolayısıyla
        // veritabanı sızarsa anahtarlar kullanılamaz.
        var hash = ComputeHash(presentedToken);
        var entity = await dbContext.ApiTokens.FirstOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (entity is null || entity.RevokedAtUtc is not null)
        {
            return null;
        }

        if (entity.ExpiresAtUtc is not null && entity.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        // Son kullanım anı: kullanılmayan anahtarları fark edip iptal edebilmek için. Her istekte
        // yazmak gereksiz yük olurdu; dakikada birden sık güncellenmiyor.
        if (entity.LastUsedAtUtc is null || DateTime.UtcNow - entity.LastUsedAtUtc > TimeSpan.FromMinutes(1))
        {
            entity.LastUsedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return entity;
    }

    private static string ComputeHash(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ApiTokenDto ToDto(PanelApiToken entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Suffix = entity.Suffix,
        AllowDestructive = entity.AllowDestructive,
        AllowTerminal = entity.AllowTerminal,
        CreatedAtUtc = entity.CreatedAtUtc,
        ExpiresAtUtc = entity.ExpiresAtUtc,
        LastUsedAtUtc = entity.LastUsedAtUtc,
        IsRevoked = entity.RevokedAtUtc is not null,
        IsUsable = entity.RevokedAtUtc is null &&
                   (entity.ExpiresAtUtc is null || entity.ExpiresAtUtc > DateTime.UtcNow)
    };
}
