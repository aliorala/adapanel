# Webmail domain/account handoff and global FTP navigation — 2026-10-28

The previous mailbox button called the generic webmail opener. The shared Roundcube
state selected its first secured host, so another domain and an existing session
could be displayed. A mailbox button now captures its domain and exact address.
The general button opens that domain's login page without selecting a mailbox.

The Stalwart/Roundcube handoff uses a random 60-second, one-use, hash-indexed ticket.
The ticket binds the actor's current authority, exact mailbox ID, domain, hostname
and panel origin. Authority is measured again before consumption. Disabled users,
revoked sessions/tokens, suspended subscriptions, aliases and domain mismatches fail.
Unknown mailbox activity is resolved by actual IMAP authentication rather than
being treated as a successful login.

The browser posts the opaque ticket to an authenticated same-origin HTML endpoint,
then to the exact HTTPS webmail hostname. The fresh handoff document supplies a
nonce CSP allowing that one destination and a strict-origin referrer policy. This
is necessary because the panel's own CSP permits only same-origin forms and its
referrer policy would otherwise suppress the cross-origin POST Origin header.
The global panel policy is preserved. No credential is put in a URL.

The private loopback broker requires its dedicated application-pool key and rejects
forwarding headers and nonlocal sockets. It creates only a target-owned 30-minute
Stalwart application password, restricted to loopback and explicit IMAP, submission
and ManageSieve permissions. Replace permissions intersect the account's existing
effective permissions in Stalwart 0.16.26. Primary passwords never change and the
administrator credential never enters PHP or the browser. A nonsecret ownership
ledger supports cleanup after failures and restarts; only owned credentials are
revoked. Roundcube replaces the previous same-host session after validating the
handoff and uses its normal IMAP login. Host-only cookies separate domains.

Before SSO, the managed Roundcube tree must pass dedicated-pool ACL verification.
Existing HTTPS bindings and managed configuration keys are preserved during repair.
The selected hostname must pass normal certificate/name/chain checks; no alternate
domain or unverified HTTP fallback can count as SSO. MailEnable is explicitly
unsupported for this SSO contract instead of pretending that a generic login page
signed in to the selected mailbox.

The dashboard FTP card now opens `section=ftp`, reusing the existing global FTP
workspace without a site filter. Administrators and resellers see all accounts
returned within their API scope, with site, folder, status, search and account
actions. Customer/operator navigation remains closed consistently with the API.

Validation before packaging: 81 targeted backend tests, PHP lint and six plugin
sandbox scenarios; 100 targeted frontend tests and vue-tsc. Replay mutation caused
40 consumptions instead of one and failed; restoring the atomic removal passed.
Routing/form-action mutations also failed. A real Chrome two-origin fixture passed
the fresh-document CSP/origin transfer, retaining only the panel origin as Referer.
The fixture does not substitute for live IIS/Stalwart mailbox authentication.

Independent review found the inherited CSP/referrer obstacles before release;
those changes were returned to the developer for correction and reviewed again.
Live deployment and exact-account verification are recorded separately when run.

Primary version contracts:

- https://raw.githubusercontent.com/roundcube/roundcubemail/1.7.0/public_html/index.php
- https://raw.githubusercontent.com/stalwartlabs/stalwart/v0.16.26/crates/common/src/auth/access_token.rs
- https://raw.githubusercontent.com/stalwartlabs/stalwart/v0.16.26/crates/smtp/src/inbound/auth.rs
- https://fetch.spec.whatwg.org/#append-a-request-origin-header
- https://html.spec.whatwg.org/multipage/browsers.html#policy-containers
