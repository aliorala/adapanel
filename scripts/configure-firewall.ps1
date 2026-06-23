param(
    [int]$Port = 8890
)

$ErrorActionPreference = "Stop"

$ruleName = "AdaPanel Panel $Port"

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Any -ErrorAction Stop | Out-Null

Write-Host "Firewall rule '$ruleName' created."

# Mail sunucusu portlari: 25=SMTP/MX (gelen + sunucular arasi), 143=IMAP, 587=Submission,
# 465=SMTPS, 993=IMAPS, 995=POP3S. Gelen postanin (ornegin Hotmail -> domain) sunucuya
# ulasabilmesi icin ozellikle 25 inbound ACIK olmali. Idempotent; hata kurulumu bozmasin.
foreach ($mailPort in 25, 143, 465, 587, 993, 995) {
    $mailRule = "AdaPanel Mail $mailPort"
    try {
        Get-NetFirewallRule -DisplayName $mailRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
        New-NetFirewallRule -DisplayName $mailRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $mailPort -Profile Any -ErrorAction Stop | Out-Null
        Write-Host "Firewall rule '$mailRule' created."
    } catch {
        Write-Warning "Mail firewall rule for port $mailPort could not be created: $_"
    }
}

# Web (site) portlari: 80=HTTP, 443=HTTPS. IIS'in varsayilan kurali cogu zaman 443'u (HTTPS) ACMAZ;
# acik degilse barindirilan sitelere HTTPS ile (ve Let's Encrypt HTTP-01 dogrulamasi 80 ile) erisilemez.
# Idempotent; hata kurulumu bozmasin.
foreach ($webPort in 80, 443) {
    $webRule = "AdaPanel Web $webPort"
    try {
        Get-NetFirewallRule -DisplayName $webRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
        New-NetFirewallRule -DisplayName $webRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $webPort -Profile Any -ErrorAction Stop | Out-Null
        Write-Host "Firewall rule '$webRule' created."
    } catch {
        Write-Warning "Web firewall rule for port $webPort could not be created: $_"
    }
}

# DNS sunucusu (Windows DNS rolu): otoriter ad sunucusu hem UDP hem TCP 53 dinler (TCP = zone transfer +
# 512 byte ustu yanitlar). Rol kuruluyken disaridan gelen sorgular ulasabilsin diye iki protokol de acilir.
# Rol kurulu degilse dinleyen yok -> acik port zararsizdir. Idempotent; hata kurulumu bozmasin.
foreach ($dnsProto in 'TCP', 'UDP') {
    $dnsRule = "AdaPanel DNS 53 $dnsProto"
    try {
        Get-NetFirewallRule -DisplayName $dnsRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
        New-NetFirewallRule -DisplayName $dnsRule -Direction Inbound -Action Allow -Protocol $dnsProto -LocalPort 53 -Profile Any -ErrorAction Stop | Out-Null
        Write-Host "Firewall rule '$dnsRule' created."
    } catch {
        Write-Warning "DNS firewall rule ($dnsProto 53) could not be created: $_"
    }
}
