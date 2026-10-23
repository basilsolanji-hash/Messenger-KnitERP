# Operator endpoints require explicit opt-in — 23 October 2026

Base: PR #5 at `59140dfe40563e856d88eaeea770c8e23243588c`.

## Behavior

`Dev:EnableOperatorEndpoints` defaults to false. Without explicit true, no dev account creation, replacement, session renewal, fake SMS code reader or OpenAPI route is mapped. A configured operator secret cannot activate the routes by itself. Ordinary SMS/PIN/refresh and authenticated messaging routes are independent of this setting.

With explicit true, startup requires the existing random operator secret of at least 40 characters. All four dev POST methods retain their secret check; OpenAPI retains bearer authorization. The fake code reader is absent with SmsRu. Invalid Boolean input fails startup. The Development-only guard remains, including when the flag is true.

`init-dev.py`, the isolated E2EE smoke launcher and PostgreSQL test host explicitly opt into synthetic operator mode. `.env.example` remains default-off. Existing `.env` files are never regenerated; operators opting into an old local environment must add the flag deliberately. README and CLOSED_TEST describe migration.

Disabling the flag requires an API restart. It removes routes but does not revoke already issued sessions. It does not implement Nginx access restrictions or replace TLS/allowlists. No live deployment or main merge occurred.

## Evidence

.NET SDK 10.0.402, locked restore and Release compilation with warnings as errors succeeded. **69 non-PostgreSQL tests passed**, including ten new actual Program HTTP/startup cases. The routing host removes the database worker only for these tests and throws/counts on database connection attempts; expected routing/auth paths made zero attempts. This is not a substitute for database integration acceptance.

The local configuration generator was executed in a disposable directory: file mode 0600, explicit opt-in, Fake SMS only, no printed secrets and no overwrite on a second invocation. Both changed Python files pass syntax compilation. See [raw TRX](../test-results/2026-10-23/server-operator-unit.trx) and [command summary](../test-results/2026-10-23/server-operator.txt).

PostgreSQL tests compiled but were not executed. Real HTTPS/pins, paid SMS and two physical phones remain pending. CI status must be checked separately from this local result.
