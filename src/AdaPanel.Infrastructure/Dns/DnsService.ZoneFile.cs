using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService
{
    /// <summary>
    /// BIND içe aktarımı için kaydı, panelin TEK kayıt doğrulamasından (<c>NormalizeRecord</c> →
    /// <c>ValidateRecordValue</c>/<c>NormalizeHost</c>) geçirir. Ayrı bir doğrulama yazılmadı: içe
    /// aktarma yolunun elle kayıt yolundan daha gevşek olması, tam da kontrol dışı bir kapı olurdu.
    /// </summary>
    /// <exception cref="AdaPanel.Core.Common.AppException">Geçersiz tür/konak/değer.</exception>
    internal static UpsertDnsRecordRequest NormalizeForImport(UpsertDnsRecordRequest request, string zoneName)
    {
        var normalized = NormalizeRecord(new DnsRecordState
        {
            Id = string.Empty,
            Type = request.Type ?? string.Empty,
            Host = request.Host ?? string.Empty,
            Value = request.Value ?? string.Empty,
            Ttl = request.Ttl,
            Priority = request.Priority
        }, zoneName);

        return new UpsertDnsRecordRequest
        {
            Type = normalized.Type,
            Host = normalized.Host,
            Value = normalized.Value,
            Ttl = normalized.Ttl,
            Priority = normalized.Priority
        };
    }
}
