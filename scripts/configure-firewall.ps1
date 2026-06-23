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
