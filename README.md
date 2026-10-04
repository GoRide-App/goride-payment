# GoRide Payment

SCRUM-101: a rider can select card payment after their trip completes. The service uses .NET 10, MySQL 8 and direct ADO.NET queries. Selection records `method: "Card"` and leaves the payment `Pending`; it does not charge a card or mark the trip paid. Provider checkout and payment verification belong to SCRUM-102/103.

## Run locally

1. Create a `payment_db` MySQL database and a service user with SELECT, INSERT and UPDATE access to its tables.
2. Apply `src/GoRide.Payment/Data/schema.sql` to that database using a schema administrator. The application never creates or changes production schemas automatically.
3. Set `ConnectionStrings__Payments`, `Identity__BaseUrl`, and `InternalServices__ApiKey` in the environment. `.env.example` lists the settings; `.env` is not loaded automatically. Alternatively use a gitignored `src/GoRide.Payment/appsettings.Development.json`.
4. Run `dotnet run --project src/GoRide.Payment --urls http://localhost:8083`.
5. `GET /health` checks the database and payments table.

Use TLS for database and identity connections outside local development. No credentials are committed. A Docker image can be built with `docker build -t goride-payment .` and run on container port 8080 with these environment variables.

## API

| Method | Path | Result |
| --- | --- | --- |
| GET | `/payments/{tripId}` | The authenticated rider's payment, or JSON `null` while completion is unavailable |
| POST | `/payments/{tripId}/select-method` | Accepts exactly `{ "method": "Card" }`; returns the pending payment |
| POST | `/internal/trip-events` | Trusted `TRIP_COMPLETED` ingestion with `X-Internal-Api-Key` |

The public paths match the frontend's existing `payments.get` and `payments.selectMethod` contract. Route these paths through the same frontend origin so its identity session cookie reaches the service. The payment service verifies the cookie against identity-auth's `/api/me`; it never accepts a browser-supplied rider ID. Its HTTP client does not store cookies or follow redirects. Configure `Identity__BaseUrl` to the actual identity service. Direct cross-origin clients additionally require an allowed CORS origin and credentials.

The trip/fare producer must publish completion with the authoritative final fare. Existing frontend mock payments are not authoritative input. No browser can register a completion event or set an amount. Integration with the running trip producer/gateway and deployment are separate from this repository's service implementation.

Example trusted completion body (send with the configured internal service key):

```json
{
  "eventId": "completed-trip-123-v1",
  "eventType": "TRIP_COMPLETED",
  "tripId": "trip-123",
  "riderId": "identity-user-sub",
  "driverId": "driver-sub",
  "occurredAt": "2026-10-04T10:00:00Z",
  "payload": {
    "estimatedFare": 600,
    "finalFare": 725.50
  }
}
```

`payload.fare` is also accepted for the existing trip-event envelope; `finalFare` takes precedence when supplied. Neither may contain a negative value, more than two decimal places, or exceed 99,999,999.99. Missing amounts are rejected. An optional breakdown must add up to the final fare. Monetary values use C# decimal and are serialized as decimal strings inside the private database document to avoid MySQL JSON floating-point conversion. API responses expose them as JSON numbers.

Errors include `status`, `title`, and a stable `code`. Invalid JSON, unknown fields and invalid methods return 400; no session returns 401; another rider returns 403; missing completion or a settled/disabled payment returns 409; identity/database outages return 503. Validation failures leave the record unchanged.

## Idempotency and fare updates

- One payment per trip is enforced by the database primary key. Concurrent selections and completion updates lock that row.
- Payment changes and the event inbox are committed in the same transaction. Retrying the same event, including after restarting the service, returns the existing payment.
- An event ID reused with different data returns `EVENT_ID_CONFLICT` and rolls back all changes.
- A newer completion event may correct the final fare while the payment is pending and keeps its selected method. Older events cannot restore an old fare. Conflicting amounts with the same timestamp are rejected.
- Settled payments cannot have their final fare rewritten. Such events require reconciliation.
- Selecting card is idempotent, does not increment attempts, and cannot bypass a disabled card or an already settled payment.

## Kafka

Enable `Kafka__Enabled=true` and set `Kafka__BootstrapServers` to consume `goride.trip.events` using the independent `goride-payment` consumer group. Event envelopes must match the example. Broker ACLs should permit only trusted trip/fare producers to write completion events. The internal HTTP endpoint and Kafka use the same transaction logic.

Offsets are committed only after database commit. Temporary failures retry at the same offset. Invalid/conflicting events stop the service without committing the offset; inspect the logged topic/partition/offset and correct or reconcile the event before restarting. This avoids silently losing payment events. Optional broker settings are `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, and `Kafka__SaslPassword`.

## Verification

```sh
dotnet restore GoRide.Payment.slnx
dotnet build GoRide.Payment.slnx --configuration Release --no-restore
dotnet test GoRide.Payment.slnx --configuration Release --no-build
```

Unit tests cover authorization rules, state transitions, exact amounts, duplicate selection, stale events, fare correction and settlement guards. HTTP tests exercise the real application and MySQL transactions, with only the upstream identity service replaced by a test handler.

Set `PAYMENT_TEST_MYSQL` to a **disposable local/CI MySQL administrator connection** before running the HTTP/database tests. Each test creates and removes its own randomly named `payment_test_...` database; it does not use `payment_db`. If this variable is absent, database tests are explicitly skipped. The GitHub Actions workflow always supplies a fresh MySQL service and runs build, all tests, artifact upload and Docker build on every branch push and PR.

Example PowerShell for a disposable server:

```powershell
$env:PAYMENT_TEST_MYSQL = 'Server=127.0.0.1;Port=3306;User ID=root;Password=local-test-password;SslMode=Disabled'
dotnet test GoRide.Payment.slnx --configuration Release --logger trx
```

Story coverage: SCRUM-651 (endpoint and business rules), SCRUM-652 (validation/errors), SCRUM-653 (unit tests), SCRUM-654 (HTTP happy path, persistence, concurrency), SCRUM-655 (CI build/tests).
