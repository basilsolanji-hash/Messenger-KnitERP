# SMS.ru adapter hardening — 23 October 2026

This increment is based on PR #2 at `09837348767dfbdc65653c28cab5e111bb89e0b4`. It does not establish release readiness.

## Result

- POST credentials and one canonical E.164 recipient to the fixed `https://sms.ru/sms/send` endpoint. No key in URL; no response/body logging added.
- The production transport refuses redirects, caps response headers at 16 KiB and does not drain discarded response bodies. No application retry is added.
- Read response headers first; cap JSON body at 16 KiB and nesting at eight levels. A linked 15-second deadline covers both headers and body, including requests sent after a client disconnect.
- Acceptance requires root and recipient `OK`/100, exactly the requested recipient and a nonempty ASCII message ID. Invalid types, duplicate keys, contradictory statuses, mismatched recipients and malformed/oversized responses become `Unknown`.
- Documented explicit refusal codes become `Rejected`; unknown codes and server error 500 stay `Unknown`. Neither acceptance nor refusal establishes actual delivery or billing.
- SMS text now says «Ось коммуникации». The committed reservation, replay behavior, verification window and retained budget reservation are unchanged. Ambiguous sends are never blindly resent.

Contract checked against the [official JSON send method](https://sms.ru/docs/api/api_group_sms/send) and [official status code reference](https://sms.ru/api/send). Configured cost estimates are not a verified provider price or a guaranteed cap on the bill. No cost quote or real-provider call was made.

## Evidence

Locked restore and Release build succeeded on .NET SDK 10.0.402. All **59 non-PostgreSQL cases passed** (24 existing and 35 new), including actual bounded stream reads, the 15-second body deadline and a real loopback 307 transport check. See [raw TRX](../test-results/2026-10-23/server-sms-unit.trx) and [command summary](../test-results/2026-10-23/server-sms.txt).

PostgreSQL tests were compiled but excluded. PostgreSQL flow tests, deployment with public HTTPS/pins, real SMS and the two-phone text acceptance remain pending. The development-only startup guard remains in place. GitHub CI results must be reported independently from this local run.
