# Independent recheck — 23 October 2026 UTC

Checked source commit: `5c475ecaf46daaae64179871421388b31aa682fd` (PR #6). All 33 server, build manifest and device-proof fixture blobs matched that GitHub commit before the checks. No application code was changed for this recheck.

## Executed

- Re-ran Release tests with .NET SDK 10.0.402: `dotnet test server/Messenger.Tests -c Release --no-restore --filter 'Category!=PostgreSQL'`. Result: **69 passed, zero failed or skipped**. [Fresh TRX](../test-results/2026-10-23/server-independent-recheck.trx).
- Started the built API with real Kestrel in two sequential independent processes bound to ephemeral loopback HTTP ports. One had operator endpoints disabled, the other explicitly enabled. Both used Fake SMS, generated ephemeral secrets, auto-migration disabled and an unavailable synthetic database address. This is routing/startup verification, not database flow acceptance or a two-process database race test.
- **16 process HTTP checks passed**: liveness 200, unauthenticated `/me` 401 and malformed public SMS JSON 400 in both modes; OpenAPI 404/401; all four operator POST routes 404 with the correct operator secret while disabled and 401 with a wrong secret while enabled. Generated operator/signing/index/code/PIN secrets were absent from the captured process logs. [Process results](../test-results/2026-10-23/process-http.json).
- `dotnet list server/Messenger.Api package --vulnerable --include-transitive --format json` exited 0; NuGet reported no known vulnerable packages for this API project at check time. This covers NuGet dependencies, not the OS/container, Android dependencies or a complete security audit. [Raw NuGet output](../test-results/2026-10-23/nuget-vulnerabilities.json).

## CI investigation

For code commit `5c475ec`, Server run `39989181351` / job `120536190878` and Android run `39989181358` / job `120536191366` report failure with `runner_id=0` and `steps=[]`. Server job logs returned 404 BlobNotFound; check annotations are not exposed by the connected GitHub tool. The exact scheduling failure cause is unestablished. This is not a green CI result, and no billing explanation is inferred.

## Remaining acceptance

The current environment is UID0 without Docker/PostgreSQL commands; earlier verified privilege restrictions block starting ordinary PostgreSQL as an unprivileged user. The PostgreSQL root guard was not bypassed. PostgreSQL-dependent cases remain excluded; this run does not establish complete SMS/PIN/refresh persistence, races or delivery acceptance.

No public test endpoint or deployment credentials are configured in the checked repository. Live HTTPS/CA/hostname/SPKI pins, real provider acceptance/delivery/billing, backup restore and two physical phones remain unverified. No main merge or live deployment was performed.
