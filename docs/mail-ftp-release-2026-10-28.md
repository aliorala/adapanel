# Mail/FTP integration and menu totals — 2026-10-28

This release combines the reviewed mail-server identity and email/database menu
counts from the identity handoff with the existing domain/account Webmail SSO and
global FTP workspace. Unrelated website/SEO edits are preserved separately.

Additional lifecycle corrections found during integration:

- Pinned mail identities also validate and repair dangling default mail-domain
  references. Repair uses an existing valid domain ID without deriving a replacement
  hostname from domain ordering. Native certificate and SNI selection are preserved.
- The first newly tracked domain certificate cannot replace an already working
  managed Roundcube/native TLS connection identity when the registry is empty.
  Successful publication preserves the measured existing name as primary metadata;
  normal cold installation can still initialize its first trusted certificate.
- Successful identity selection persists and reads back the certificate contact
  even when the valid Windows certificate is reused. Failed activation preserves
  the earlier preference/contact and verifies rollback where possible.
- FTP preparation preserves measured passive ports and external IPv4 instead of
  resetting them when provisioning a new account or repairing the service. Only
  the panel's own passive firewall rule is synchronized with the actual range.
- Definitive FTP TLS failures stop account creation before Windows account changes.
  Unknown measurements remain distinct from measured failure. Unknown passive
  ranges are returned as null and displayed as an em dash.

Menu totals use measured, role-scoped inventories: email, FTP, databases and
customer/reseller accounts. Staff and additional subscription users are excluded
from customer/reseller totals. Failed or late reads cannot overwrite another
actor's count or claim an unmeasured zero. Existing site/software/attention badges
keep their original meanings.

No PTR-provider instruction interface is added. The installation keeps a generic
product default; the requested live server preference is applied after deployment.

Before deployment, strict network probes found that mail.adapanel.host presented
the existing VideoTools certificate and greeting. The Travian and VideoTools
hostnames both passed certificate SAN, validity and trust checks on 587/465.
The current common FTPS hostname adapanel.host passed AUTH TLS, certificate name,
validity and trust checks without credentials. These are baseline observations;
post-deployment results are recorded after the live checks complete.

Pre-release checks passed: 346 mail/certificate/identity/Webmail regressions plus
two activation/rollback cases, 112 focused FTP backend cases, 228 frontend cases
across twelve relevant files, vue-tsc and an isolated production frontend build.
Regression mutations for primary identity, pinned domain repair, contact
persistence and passive-range reset were detected. Independent read-only review
approved both mail and FTP changes.

Live deployment of 0.8.135 exposed an imported-site association defect: the
managed domain adapanel.host is bound to the technical IIS site web-adapanel.host.
The identity flow resolved that binding, but its ACME helper repeated a lookup
by site name and failed before starting win-acme. The working identity was kept.

The 0.8.136 follow-up uses the same unambiguous domain-to-IIS association for
identity validation and certificate issuance, passes the measured native site ID
to win-acme, and verifies the exact HTTP-01 binding after adding it. Duplicate or
conflicting associations stop before issuance. Cancellation propagates. Failure
details preserve known application error keys and bounded, redacted ACME output;
private keys, credentials and challenge tokens are masked before truncation.
Independent read-only review approved the follow-up source and test coverage.

The final 0.8.136 focused identity, ACME diagnostics and certificate lifecycle
group passed 87/87. Two deliberate regressions (site-name lookup and swallowed
issuer error) were both caught; original production sources were restored before
the final passing run. The isolated production frontend build also passed.

Live verification completed with 0.8.136 on the home server:

- GitHub release 0.8.136 installer was downloaded again and matched SHA-256
  CEEBC044EE1505DAA382690839DFFEAA98E7999279F6AC943940DF13BF037090
  (91,237,958 bytes). The updater confirmed the running version; installation
  history records 2026-10-28 23:15:06 +03:00.
- Applied managed domain adapanel.host, hostname mail.adapanel.host and contact
  info@adapanel.host. Reloaded form readback retained all three selections.
- mail.adapanel.host now presents the trusted Let's Encrypt certificate with SAN
  mail.adapanel.host, thumbprint CA539340BC18DDE6B625EEEDA1310D09B1CF3112,
  expiry 2027-01-26 19:21:25 UTC. Real 220 greeting and post-TLS EHLO name are
  mail.adapanel.host.
- Strict external TLS tests passed on both 587 STARTTLS and 465 implicit TLS for
  mail.adapanel.host, mail.travianpanel.com and mail.videotools.com.tr. SAN, time,
  online chain verification and SMTP/EHLO were required, with no validation bypass.
  The two pre-existing domains retained their baseline certificate thumbprints.
- The panel's automatic domain repair completed (four domains already current).
  All six strict TLS/SMTP tests passed again afterward. Stalwart was then restarted
  through Server Health; all six passed afterward and the stored identity/contact
  remained selected. No other services were restarted for this check.
- Initial rapid connection bursts produced isolated SMTP closes after a valid TLS
  handshake. Pacing the diagnostic connections one second apart produced 6/6 passes
  before repair, after repair and after restart; server protection settings were
  not weakened. Initial raw reports were retained alongside final reports.
- Existing mailbox-row Webmail sign-in opened info@travianpanel.com on
  webmail.travianpanel.com and info@adapanel.host on webmail.adapanel.host, without
  password entry/reset. Account headers and mailbox listings loaded after restart;
  no message bodies were opened or modified by these checks.
- After installing 0.8.136, public and LAN FTPS paths both passed trusted TLS,
  directory listing, an owned UUID probe-file upload/download SHA-256 round trip
  and deletion of that probe file. Existing site files were not replaced.
- Live menu totals: sites 4, mailboxes 4, FTP accounts 2, databases 5,
  customers/resellers 0 (only the administrator account currently exists).

No new PTR instructions UI was added. Mailbox passwords, MX/DKIM values and
pre-existing domain certificates were not intentionally changed. This verifies
SMTP TLS and greeting, not end-to-end delivery to an external recipient. Optional
Outlook autodiscovery publishing remains a separate unconfigured feature on the
two inspected domains; manual client settings and the tested Webmail login work.
