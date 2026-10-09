# GoRide Payment

SCRUM-101/102: a rider can select card payment after their trip completes and continue to Stripe's hosted checkout. The service uses .NET 10, MySQL 8 and direct ADO.NET queries. This implementation accepts **Stripe test secret keys only**. Opening or returning from checkout leaves the payment `Pending`; verification and marking a payment paid belong to SCRUM-103.

## Run locally

1. Create a `payment_db` MySQL database and a service user with SELECT, INSERT and UPDATE access to its tables.
2. Apply `src/GoRide.Payment/Data/schema.sql` to that database using a schema administrator. Run it again when upgrading from SCRUM-101: it adds `payment_checkouts` without modifying existing tables. The application never creates or changes production schemas automatically.
3. Set `ConnectionStrings__Payments`, `Identity__BaseUrl`, and `InternalServices__ApiKey` in the environment. `.env.example` lists the settings; `.env` is not loaded automatically. Alternatively use a gitignored `src/GoRide.Payment/appsettings.Development.json`.
4. Run `dotnet run --project src/GoRide.Payment --urls http://localhost:8083`.
5. `GET /health` checks the database, payments table and checkout table.

Use TLS for database and identity connections outside local development. No credentials are committed. A Docker image can be built with `docker build -t goride-payment .` and run on container port 8080 with these environment variables.

## API

| Method | Path | Result |
| --- | --- | --- |
| GET | `/payments/{tripId}` | The authenticated rider's payment, or JSON `null` while completion is unavailable |
| POST | `/payments/{tripId}/select-method` | Accepts exactly `{ "method": "Card" }`; returns the pending payment |
| POST | `/payments/{tripId}/checkout` | Accepts `{}`; returns a validated Stripe checkout URL and session details |
| POST | `/internal/trip-events` | Trusted `TRIP_COMPLETED` ingestion with `X-Internal-Api-Key` |

The get/select paths match the frontend's existing `payments.get` and `payments.selectMethod` contract. The new checkout endpoint returns JSON for the client to navigate with `window.location.assign(response.url)`. Route these paths through the same frontend origin so its identity session cookie reaches the service. The payment service verifies the cookie against identity-auth's `/api/me`; it never accepts a browser-supplied rider ID. Its HTTP client does not store cookies or follow redirects. Configure `Identity__BaseUrl` to the actual identity service. Direct cross-origin clients additionally require an allowed CORS origin and credentials.

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

## SCRUM-102: Stripe test checkout

Configure `Stripe__SecretKey` with a sandbox `sk_test_...` key in the process environment or the ignored `appsettings.Development.json`. A publishable key is not needed: all session creation happens on the server, and card entry happens on Stripe. Set `Stripe__SuccessUrl` and `Stripe__CancelUrl` to trusted return pages. URLs must use HTTPS, except HTTP loopback URLs in Development. Client-supplied amount, currency, identity, return URLs, and card details are rejected.

An authenticated rider first selects Card, then calls:

```http
POST /payments/trip-123/checkout
Content-Type: application/json

{}
```

The response contains `sessionId`, `url`, `amount`, `currency` (`LKR`) and `expiresAt`. It has `Cache-Control: no-store`. Only HTTPS URLs on `checkout.stripe.com` are returned. The amount is derived from the stored final fare using exact integer minor units. Card checkout supports positive fares up to LKR 999,999.99; Stripe also enforces its account/currency minimums. The Stripe API version is pinned to `2025-02-24.acacia`.

Checkout attempts, including their amount, return URLs and idempotency key, are committed before any provider call. MySQL trip locks serialize session creation with completion/fare updates across service instances. Duplicate requests reuse an open session. A lost provider response recovers with the same idempotency key after a restart. If an unknown outcome is over 23 hours old, the service requires reconciliation instead of risking a duplicate after Stripe's idempotency retention window. Known expired sessions can be replaced; completed sessions block further attempts pending SCRUM-103 verification.

A fare correction must first confirm the previous session is expired. Provider failure or an already completed session leaves the old amount unchanged and does not consume the correction event. Retrying the same correction can then finish safely. Same-fare or older completion events preserve the existing checkout. Browser success/cancel query parameters never change payment state.

Additional error codes include `CARD_NOT_SELECTED`, `CHECKOUT_BUSY`, `CHECKOUT_AWAITING_VERIFICATION`, `CHECKOUT_RECONCILIATION_REQUIRED` (409), `INVALID_CHECKOUT_RESPONSE` (502), and `STRIPE_TEST_KEY_REQUIRED`/`CHECKOUT_PROVIDER_UNAVAILABLE` (503). Provider error bodies and credentials are not returned to the browser.

### Development page

Run `dotnet run --project src/GoRide.Payment` and open **http://localhost:8083/dev/payments**. The checked-in launch profile enables Development and the local test page. Configure the database and Stripe sandbox key first. The page creates an isolated `dev-trip-...` fixture, selects Card, shows its final fare and opens real Stripe hosted checkout. It needs neither identity-auth nor the trip producer for this local test flow.

Use Stripe's test card `4242 4242 4242 4242`, a future expiry and a three-digit CVC. Cancel returns to the page and reuses the same open session. Returning after payment only displays a verification-pending notice; it does not mark the ride paid.

Development assets and fixture endpoints exist only when **both** the environment is `Development` and `DevelopmentCheckout__Enabled=true`. They require a loopback connection and host. Mutation requests additionally require the page's custom header and same-origin check. They cannot access another rider's payments or bypass authentication on the public payment API. No API key is served to the browser. Deployment/staging should leave this flag false and use HTTPS return pages.

Provider references: [create Checkout Sessions](https://docs.stripe.com/api/checkout/sessions/create), [idempotent requests](https://docs.stripe.com/api/idempotent_requests), [expire a session](https://docs.stripe.com/api/checkout/sessions/expire), [test cards](https://docs.stripe.com/testing).

## Kafka

Enable `Kafka__Enabled=true` and set `Kafka__BootstrapServers` to consume `goride.trip.events` using the independent `goride-payment` consumer group. Event envelopes must match the example. Broker ACLs should permit only trusted trip/fare producers to write completion events. The internal HTTP endpoint and Kafka use the same transaction logic.

Offsets are committed only after database commit. Temporary failures retry at the same offset. Invalid/conflicting events stop the service without committing the offset; inspect the logged topic/partition/offset and correct or reconcile the event before restarting. This avoids silently losing payment events. Optional broker settings are `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, and `Kafka__SaslPassword`.

## Verification

```sh
dotnet restore GoRide.Payment.slnx
dotnet build GoRide.Payment.slnx --configuration Release --no-restore
dotnet test GoRide.Payment.slnx --configuration Release --no-build
```

Tests cover authorization, validation, exact amounts, concurrent checkout, lost responses, restart recovery, stale events, fare corrections, provider failures, unsafe redirects, session expiration, settlement guards and development-page isolation. HTTP tests exercise the real application, Stripe HTTP adapter and MySQL data access with deterministic identity/Stripe HTTP stubs. They do not need real API keys. Real Stripe sandbox/browser verification is a separate manual check using local ignored credentials.

Set `PAYMENT_TEST_MYSQL` to a **disposable local/CI MySQL administrator connection** before running the HTTP/database tests. Each test creates and removes its own randomly named `payment_test_...` database; it does not use `payment_db`. If this variable is absent, database tests are explicitly skipped. The GitHub Actions workflow always supplies a fresh MySQL service and runs build, all tests, artifact upload and Docker build on every branch push and PR.

Example PowerShell for a disposable server:

```powershell
$env:PAYMENT_TEST_MYSQL = 'Server=127.0.0.1;Port=3306;User ID=root;Password=local-test-password;SslMode=Disabled'
dotnet test GoRide.Payment.slnx --configuration Release --logger trx
```

Story coverage: SCRUM-651–655 (card selection) and SCRUM-656–663 (checkout API/business logic, validation, parameterized ADO.NET, unit/HTTP/database tests and CI). SCRUM-664 is the staging deployment/health check **after merge**; pushing this branch does not perform a staging deployment.
