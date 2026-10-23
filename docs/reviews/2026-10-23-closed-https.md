# Closed HTTPS preparation — 23 October 2026

Base: PR #6, `01488e311ec14f55cfb2982bf4a3fb83578a11f9`. No existing Android/server runtime files were changed.

Added an offline generator for a dedicated Nginx configuration with explicit tester/operator IP allowlists, TLS 1.2/1.3, fixed loopback upstream, overwritten X-Forwarded-For and disabled proxy retries. Operator routes match without case sensitivity before general API routes. Generated endpoint/pins JSON contains public data only; private key is referenced, not copied. Inputs, certificate hostname/DNS SAN/time/key match, distinct backup SPKI, output permissions and no-overwrite behavior are checked. It never installs or deploys the output.

The main configuration is isolated from the host's existing global/site config to avoid inherited real_ip/route settings. It runs foreground/single-process for the closed test and is intended for a dedicated unprivileged service user; installation, service permissions/capability and public port acceptance remain operator work, not completed deployment. The runbook explains Docker peer IP trust and the retained Development/operator guards.

## Executed

**21 Python tests passed**: 11 generator cases and 10 integration cases against real local Nginx. Subcases include mixed-case/encoded/double-slash/dot-segment operator paths, spoofed XFF, forbidden peers, unknown routes/host, CA/hostname errors, TLS 1.2/1.3 success and TLS 1.1 rejection. Independent curl pinning accepted the current key and rejected backup-only before sending HTTP. The upstream is a simple HTTP fixture, not the messenger API or PostgreSQL.

Nginx `1.24.0-2ubuntu7.18` was extracted into scratch from the official Ubuntu security package: InRelease signature checked with the installed Ubuntu archive keyring, Packages.xz and .deb SHA256 verified. No host package installation or live service changes. Python 3.12.14, OpenSSL 3.0.13, curl 8.5.0. The UID0/capability-free fixture adds `user root;` only on its Nginx command line to prevent chown to an unmapped UID; the generated config has no root-user directive. Nginx syntax was checked before real HTTPS requests. Both changed Python files pass py_compile.

Raw output: [closed-https.txt](../test-results/2026-10-23/closed-https.txt); [package provenance](../test-results/2026-10-23/closed-https-nginx-provenance.json). A dedicated CI workflow was added; its execution must be checked separately.

A negative control altered only the in-memory generated operator location to case-sensitive matching. The real-Nginx operator-path test then detected two expected bypass subcases. The safe generator source was never changed (SHA256 checked before/after). See [expected failing negative control](../test-results/2026-10-23/closed-https-negative-control.txt); it is separate from the 21 passing safe-configuration cases.

## Not established

No public domain/CA/phone acceptance, PostgreSQL flows, backup restore, real SMS or performance claim. Previous 69 server tests were not rerun: existing server/build files were unchanged. Existing CI scheduling failures remain a separate blocker; no green CI claim. No main merge, DNS/firewall change, paid resource or deployment.
