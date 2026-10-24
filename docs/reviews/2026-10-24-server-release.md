# Server Release kit — 24 October 2026

Base: PR #7 at `a0242d3562d04fb5c9310b0bc77663466b0a8883`. Restored 40 server/build/script fixture files and checked their Git blob hashes before changes. Existing API/Android code is unchanged. Compiled application artifact corresponds to this base runtime source; new deployment/backup code is delivered separately in this PR.

## Built and executed

Official .NET SDK 10.0.402 Linux x64 archive SHA512 verified against Microsoft's release metadata. Locked restore succeeded without package-lock changes. `dotnet publish server/Messenger.Api -c Release --no-restore /p:UseAppHost=false` succeeded; output is framework-dependent API binaries, not a Docker image or APK. Release warnings remain errors.

**69 non-PostgreSQL server tests passed**, no failures/skips. A separate Kestrel process launched from the actual published directory passed four loopback HTTP checks: live 200, unauthenticated /me 401, disabled OpenAPI/dev routes 404. No generated test secrets were present in its log. PostgreSQL/real-provider access was not used.

The Release ZIP has per-file SHA256 and a source marker, contains no deployment .env/identity/TLS keys, includes license notices and setup limitations. ZIP integrity and all member hashes verified. Artifact filename/size/SHA256 are in [artifact.json](../test-results/2026-10-24/artifact.json); checksums are not digital signatures.

## Deployment and backup changes

Added separate Compose/HTTPS/backup service templates, a daily Moscow-time backup timer, and a minimal restart-policy Compose overlay. HTTPS is a dedicated unprivileged user with only CAP_NET_BIND_SERVICE; Compose/backup require administrative Docker access. Four unit files passed offline systemd-analyze syntax verification after substituting missing executables/external dependencies for this environment. This is not a VPS installation, service-user permission, boot or restart test. Overlay/workflow YAML parsed; no Docker daemon is available here.

`server-backup.py` encrypts a custom-format pg_dump plus private .env/Data Protection state with native age X25519 recipients. Private restoration key stays off the backup server. API quiescence is explicit, concurrent backups from the checkout are locked out, initial API run state is restored even after command failure, and partial outputs are not published. Encrypted state binds the ciphertext DB digest and backup ID. Offline verify authenticates/decrypts fully, rejects unsafe tar members and never restores a live database or overwrites an existing output.

**12 backup tests passed** using real age 1.1.1 encryption/decryption and native ephemeral keys. Docker/pg_dump are explicit process fixtures with synthetic bytes, not PostgreSQL. Cases include corrupt ciphertext even after public hash update, valid-part swapping, error cleanup/restart, lock contention, private modes, output preservation, traversal/link/duplicate tar entries and decryption size limits. The fixture bytes are not a valid database dump. Age package SHA256 and signed Ubuntu archive metadata were checked; no host package installation.

GPG was not used in the final design. The new script uses age directly and needs no agent/socket/keyring on the backup host. Existing HTTPS CI workflow now installs age before discovering both suites; hosted execution later passed 33/33 cases on fd988e6 (run 40071707324).

## Still required

Actual Docker image build/Compose validation, restore into a separate empty PostgreSQL instance/volume, service permissions/boot, public HTTPS/CA/pins, offsite copy/retention, real SMS and two physical phones. API and database remain loopback/internal; Development guard, Fake SMS and operator opt-in are retained. No main merge, public deployment, DNS/firewall mutation or paid service call. Full product readiness is not established by this Release build.

Evidence: [build log](../test-results/2026-10-24/build.log), [TRX](../test-results/2026-10-24/server-build.trx), [backup tests](../test-results/2026-10-24/backup-tests.log), [published process checks](../test-results/2026-10-24/publish-smoke.json), [unit syntax scope](../test-results/2026-10-24/unit-syntax.log). Deployment instructions: [deploy/README.md](../../deploy/README.md).

## Hosted CI follow-up on fd988e6

The previous runner scheduling blocker cleared. Server run 40071707357/job 120806356682 passed all **104 tests**, zero failed/skipped, against the standard PostgreSQL **18.6** service. This includes the 69 non-PostgreSQL cases plus 35 PostgreSQL cases. NuGet vulnerability check also passed without reported vulnerable API packages. Closed HTTPS run 40071707324/job 120806356546 passed **33 tests** (21 gateway and 12 backup), with a real Nginx and age installed by the runner. Summaries are committed under docs/test-results/2026-10-24. These results do not establish two independent API processes sharing a DB, actual backup restore or VPS/phone acceptance.

Android run 40071707361 stopped at setup-android before Gradle because its default sdkmanager package `tools` is no longer available. Upstream action.yml confirms packages defaults to `tools platform-tools`. The workflow now explicitly requests `platform-tools`; existing explicit API36/build-tools installation and all test/smoke steps remain. License acceptance behavior is unchanged; verbose license logging is disabled. YAML parsed locally; the corrected hosted run must be observed separately. No Android application source changed.
