using System.Text;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// CSR (sertifika imzalama istegi) uretimi ve CA imzali sertifikanin ice aktarimi. Yalniz yonetici:
// ozel anahtar sunucuda korunur ve sertifika siteye baglanir (sunucu geneli sonuc).
// DENETIM: her uc kayit yazar. OZEL ANAHTAR HICBIR YANITA/KAYDA GIRMEZ.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/ssl-csr")]
public sealed class SslCsrController(ISslCsrService csrService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SslCsrDto>>>> ListAsync(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyCollection<SslCsrDto>>.Ok(await csrService.ListAsync(cancellationToken)));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SslCsrDto>>> CreateAsync(
        [FromBody] CreateSslCsrRequest request, CancellationToken cancellationToken)
    {
        var csr = await csrService.CreateAsync(request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ssl.csr.created", $"ssl-csr:{csr.Id}",
            $"host={csr.HostName}; names={csr.AltNames.Length + 1}; keySize={csr.KeySize}");
        return Ok(ApiResponse<SslCsrDto>.Ok(csr, "ssl.csr_created"));
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        var csr = await csrService.GetAsync(id, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ssl.csr.downloaded", $"ssl-csr:{csr.Id}", $"host={csr.HostName}");
        return File(Encoding.ASCII.GetBytes(csr.CsrPem), "application/pkcs10", $"{csr.HostName.Replace("*", "wildcard")}.csr");
    }

    [HttpPost("{id}/import")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> ImportAsync(
        string id, [FromBody] ImportSignedCertificateRequest request, CancellationToken cancellationToken)
    {
        var certificate = await csrService.ImportSignedAsync(id, request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ssl.csr.imported", $"ssl:{certificate.SiteName}",
            $"csr={id}; host={certificate.HostName}:{certificate.Port}; thumbprint={certificate.Thumbprint}; " +
            $"notAfter={certificate.NotAfterUtc:yyyy-MM-dd}");
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.csr_imported"));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await csrService.DeleteAsync(id, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ssl.csr.deleted", $"ssl-csr:{id}", "Pending CSR and its protected key removed.");
        return Ok(ApiResponse<object>.Ok(new { id }, "ssl.csr_deleted"));
    }
}
