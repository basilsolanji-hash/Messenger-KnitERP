# Verified server Release kit — 24 October 2026

Application source base: PR #7, a0242d3562d04fb5c9310b0bc77663466b0a8883. Existing API/Android application source is unchanged. This kit adds deployment/backup tooling, hosted acceptance and a correction to Android SDK setup.

## Build and delivery

Official .NET SDK 10.0.402 Linux x64 archive SHA512 verified. Locked restore and Release publish succeeded without package-lock changes; warnings remain errors. The downloadable framework-dependent API ZIP has per-file SHA256, source marker, license notices and setup limitations; integrity/member hashes verified. It contains no deployment .env/identity/TLS keys. Artifact metadata: test-results/2026-10-24/artifact.json. The ZIP is not an APK or OCI image and needs a compatible ASP.NET Core runtime/database/environment.

Local verification: 69 non-PostgreSQL tests passed and four checks against an actual Kestrel process launched from publish output passed. No generated secrets appeared in its log. Raw log/TRX/publish-smoke.json are committed.

## Hosted results

- Server run 40071707357/job 120806356682 on fd988e6: **104 passed**, zero failed/skipped, with standard PostgreSQL **18.6**. This includes the 69 non-PostgreSQL and 35 PostgreSQL cases. NuGet reported no vulnerable API packages at check time.
- Closed HTTPS run 40071707324/job 120806356546: **33 tests passed**, with actual local Nginx and age (21 gateway, 12 snapshot tests). Local age encryption tests also passed 12/12; Docker there was an explicit process fixture.
- Container/restore run **40072803044**, job **120809688272**, on **f68c82c**: actual source Docker image built, Compose/API started, a synthetic opaque envelope and real Data Protection cursor created, API quiesced for encrypted backup, and age verification completed. Authenticated dump restored into a brand-new network-isolated PostgreSQL instance/volume. Asserted table counts accounts/devices/envelopes = 1/1/1, .env round trip and nonempty recovered Data Protection key XML. Original API accepted its original session/cursor after restart. This is not a new API running on the restored DB, valid Signal ciphertext or a phone test.
- Android run **40072241060**, job **120807994067**, on **5e86ece**: core/network JVM tests, debug app and instrumentation APK builds passed. Actual core:serverSmoke and network:serverSmoke with libsignal, HTTP API and PostgreSQL also passed. Android instrumentation itself was not executed. The workflow explicitly requests platform-tools instead of the removed SDK tools package; all test/smoke steps remain.

Run/job summaries are committed under docs/test-results/2026-10-24. Counts describe separate suites, not a feature completion percentage. Source-runtime files were unchanged between the cited kit commits. Hosted job outputs are independent of local execution; no blanket claim of future/current pending checks is made.

## Deployment and snapshots

Separate Compose/HTTPS/backup unit templates, a Moscow-time daily timer and a minimal restart-policy overlay were added. HTTPS uses a dedicated unprivileged user and CAP_NET_BIND_SERVICE; Compose/backup require Docker administrator privileges. Four units passed offline syntax verification with executable/external dependency substitutions. Permission/boot/start acceptance on the user's VPS is still required; the base Compose image/stack was tested in hosted CI, while the restart overlay was YAML-parsed locally.

server-backup.py uses native age X25519 public recipients and keeps the private recovery identity off the backup server. Explicit API quiescence, a per-checkout lock, prior run-state recovery, atomic publication, DB/state binding, authenticated decryption and safe tar extraction are covered. No automatic live database restore or existing-output overwrite. Local 12-case tests include tamper, part swapping, lock contention, cleanup/restart, unsafe archives and output limits. Actual PostgreSQL restoration was subsequently exercised by the hosted container acceptance above.

The CI-only container helper refuses local/VPS use, existing environments and existing source-project containers. It waits for the final PostgreSQL TCP server/SELECT, avoiding the temporary init-server readiness race. Only its own ephemeral restore container/volume is removed; there is no volume prune or down -v. No deployment secrets appear in its report.

## Remaining acceptance

VPS/DNS/firewall/service permissions/boot, public HTTPS/CA/hostname/pins from actual phones, actual user SMS/provider billing, two physical Android phones, offsite copy/retention and a complete new-API recovery/failover exercise. No main merge or public deployment was performed. Development/Fake/operator guards remain. User instructions: deploy/README.md and docs/CLOSED_HTTPS.md.
