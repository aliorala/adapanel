using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Sunucu-geneli DNS şablonu (Plesk "DNS template" gibi): yeni site bölgeleri bu şablonla seed edilir. Admin-only.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/dns/template")]
public sealed class DnsTemplateController(IDnsService dnsService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsTemplateDto>.Ok(await dnsService.GetTemplateAsync(cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<DnsTemplateDto>>> UpdateAsync(
        [FromBody] UpdateDnsTemplateRequest request,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<DnsTemplateDto>.Ok(await dnsService.UpdateTemplateAsync(request, cancellationToken), "dns.template_saved"));
}
