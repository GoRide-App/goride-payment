# GoRide Payment

SCRUM-101–104: a rider can select card payment after their trip completes, pay through the **PayHere sandbox** checkout, the trip is marked paid only after PayHere's server notice is verified, and the rider app then shows a one-time in-app confirmation. The service uses .NET 10, MySQL 8 and direct ADO.NET queries. Checkout is fixed to `https://sandbox.payhere.lk`, so no live merchant key is needed or accepted.

## Run locally

1. Create a `payment_db` MySQL database and a service user with SELECT, INSERT and UPDATE access to its tables.
2. Apply `src/GoRide.Payment/Data/schema.sql` to that database using a schema administrator. Run it again when upgrading from SCRUM-101: it adds new tables (`payment_checkouts`, `payment_verifications`, `payment_confirmations`) without modifying existing ones. The application never creates or changes production schemas automatically.
3. Set `ConnectionStrings__Payments`, `Identity__BaseUrl`, and `InternalServices__ApiKey` in the environment. `.env.example` lists the settings; `.env` is not loaded automatically. Alternatively use a gitignored `src/GoRide.Payment/appsettings.Development.json`.
4. Run `dotnet run --project src/GoRide.Payment --urls http://localhost:8083`.
5. `GET /health` checks the database and the payments, checkout, verification and confirmation tables.

Use TLS for database and identity connections outside local development. No credentials are committed. A Docker image can be built with `docker build -t goride-payment .` and run on container port 8080 with these environment variables.

## API

| Method | Path | Result |
| --- | --- | --- |
| GET | `/payments/{tripId}` | The authenticated rider's payment, or JSON `null` while completion is unavailable |
| POST | `/payments/{tripId}/select-method` | Accepts exactly `{ "method": "Card" }`; returns the pending payment |
| POST | `/payments/{tripId}/checkout` | Accepts `{}`; returns the signed PayHere sandbox form for the trip's final fare |
| POST | `/payments/payhere/notify` | PayHere's server-to-server notify URL (form encoded, verified by `md5sig`) |
| GET | `/payments/{tripId}/confirmation` | The rider's in-app confirmation: `Confirmed` with the receipt, or `Pending` |
| POST | `/payments/{tripId}/confirmation/acknowledge` | Accepts exactly `{ "confirmationId": "..." }`; records that the app showed it |
| POST | `/internal/trip-events` | Trusted `TRIP_COMPLETED` ingestion with `X-Internal-Api-Key` |

The get/select paths match the frontend's existing `payments.get` and `payments.selectMethod` contract. The checkout endpoint returns JSON (`orderId`, `actionUrl`, `fields`, `amount`, `currency`); the client builds a hidden form from `fields` and POSTs it to `actionUrl`. Route these paths through the same frontend origin so its identity session cookie reaches the service. The payment service verifies the cookie against identity-auth's `/api/me`; it never accepts a browser-supplied rider ID. Its HTTP client does not store cookies or follow redirects. Configure `Identity__BaseUrl` to the actual identity service. Direct cross-origin clients additionally require an allowed CORS origin and credentials.

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

## Card checkout (PayHere sandbox)

Configure `PayHere__MerchantId` and `PayHere__MerchantSecret` from a free PayHere **sandbox** merchant account (sandbox.payhere.lk) in the process environment or the ignored `appsettings.Development.json`. Set `PayHere__ReturnUrl`, `PayHere__CancelUrl` and `PayHere__NotifyUrl`; they must use HTTPS, except HTTP loopback URLs in Development. The checkout address is the constant `https://sandbox.payhere.lk/pay/checkout`. Client-supplied amount, currency, identity, URLs, order IDs and card details are rejected.

An authenticated rider first selects Card, then calls `POST /payments/{tripId}/checkout` with `{}`. The response (`Cache-Control: no-store`) contains the PayHere form fields, including `hash = UPPER(MD5(merchant_id + order_id + amount + currency + UPPER(MD5(merchant_secret))))`. The secret itself never leaves the server. The amount is the stored final fare in LKR with exactly two decimals. Customer contact fields come from the verified identity session, with sandbox placeholders for anything identity does not provide.

Each order is recorded before it is returned. Repeated requests reuse the same order while the fare is unchanged (MySQL trip locks serialize this across instances). A fare correction starts a new order; a payment later made against the old order is caught by verification because its amount no longer matches.

## SCRUM-103: verify PayHere before marking paid

PayHere posts the result to `notify_url` (form encoded) and passes **no** status to `return_url`, so returning from checkout never changes payment state. `POST /payments/payhere/notify` is anonymous because PayHere's server calls it; trust comes only from the signature:

1. The form is validated strictly: one value per field, PayHere's formats for `merchant_id`, `order_id`, `payment_id`, `payhere_amount` (two decimals), `payhere_currency`, `status_code` and `md5sig`, at most 40 fields and 16 KB.
2. `merchant_id` must be ours and `md5sig = UPPER(MD5(merchant_id + order_id + payhere_amount + payhere_currency + status_code + UPPER(MD5(merchant_secret))))` must match (constant-time comparison).
3. The order must exist; it fixes the trip and the expected amount.
4. Under the trip lock, the notice and its effect commit in one transaction. `status_code = 2` marks the trip `Paid` only when the paid amount and currency equal **both** the order and the current final fare. The record stores `processedAt`, `providerPaymentId` and `providerOrderId`.

Every notice is stored once in `payment_verifications`, keyed by (provider, payment ID, status code). PayHere redeliveries return the original outcome without changing anything; the same key with different details is rejected with `NOTICE_CONFLICT`. Card holder name and expiry are not stored.

| Outcome | Meaning |
| --- | --- |
| `Paid` | Verified success; the trip is now paid |
| `Pending`, `Cancelled`, `Failed` | Recorded; payment unchanged. A pending notice holds new checkouts (`CHECKOUT_AWAITING_VERIFICATION`) |
| `AmountMismatch` | Signed success for an amount or currency other than the final fare (e.g. an order superseded by a fare correction). Not marked paid; needs reconciliation |
| `DuplicatePayment` | A second, different PayHere payment for an already paid trip. Needs a refund through reconciliation |
| `Chargedback` | Recorded for reconciliation |

Notices that need reconciliation are logged as warnings, return `reconciliationRequired: true`, and block further checkout for that trip with `CHECKOUT_RECONCILIATION_REQUIRED`. Recorded notices always get HTTP 200 so PayHere stops retrying.

Notify errors (structured `status`/`title`/`code`, never echoing submitted values): `INVALID_NOTIFICATION` (400), `INVALID_SIGNATURE`/`UNKNOWN_MERCHANT` (401), `ORDER_NOT_FOUND` (404), `NOTICE_CONFLICT` (409), `NOTIFICATION_TOO_LARGE` (413), `UNSUPPORTED_MEDIA_TYPE` (415) and `PAYHERE_NOT_CONFIGURED`/`PAYMENT_STORE_UNAVAILABLE` (503). Checkout adds `CARD_NOT_SELECTED`, `CHECKOUT_BUSY`, `CHECKOUT_AWAITING_VERIFICATION`, `CHECKOUT_RECONCILIATION_REQUIRED` (409) and `CHECKOUT_NOT_CONFIGURED` (503).

PayHere cannot reach `localhost`. To receive real sandbox notices, expose the service through an HTTPS tunnel and set `PayHere__NotifyUrl` to the public URL.

### Development page

Run `dotnet run --project src/GoRide.Payment` and open **http://localhost:8083/dev/payments**. The checked-in launch profile enables Development and the local test page. Configure the database and the PayHere sandbox credentials first. The page creates an isolated `dev-trip-...` fixture, selects Card, shows its final fare and opens the real PayHere sandbox checkout (sandbox test card `4916 2175 0161 1292`, any future expiry and CVC). Because PayHere cannot call localhost, step 03 **Simulate successful payment** signs the notice PayHere would send for the open order and runs it through the same parsing and verification code as the notify URL.

Development assets and fixture endpoints exist only when **both** the environment is `Development` and `DevelopmentCheckout__Enabled=true`. They require a loopback connection and host. Mutation requests additionally require the page's custom header and same-origin check, and the page may only post forms to the PayHere sandbox (CSP `form-action`). They cannot access another rider's payments or bypass authentication on the public payment API. No secret is served to the browser. Deployment/staging should leave this flag false and use HTTPS URLs.

Provider references: [PayHere Checkout API](https://support.payhere.lk/api-&-mobile-sdk/checkout-api), [sandbox and testing](https://support.payhere.lk/sandbox-and-testing).

## SCRUM-104: in-app confirmation on card success

PayHere passes no payment status to `return_url`, so after the rider returns from checkout the app polls `GET /payments/{tripId}/confirmation` (`Cache-Control: no-store`):

```json
{
  "status": "Confirmed",
  "confirmation": {
    "confirmationId": "4f7c2c9e-3b1a-4f0e-9d8a-2a6f1c0b7e51",
    "tripId": "trip-123",
    "amount": 725.50,
    "currency": "LKR",
    "method": "Card",
    "cardBrand": "VISA",
    "cardLast4": "1292",
    "providerReference": "320027150501",
    "paidAt": "2026-10-07T08:15:00+00:00",
    "acknowledgedAt": null
  },
  "lastProviderOutcome": null
}
```

While the payment is not verified the response is `{ "status": "Pending", "confirmation": null, "lastProviderOutcome": ... }`, where the last outcome (`Pending`, `Failed`, `Cancelled`, `AmountMismatch`, ...) lets the app explain a failed or held payment instead of waiting.

The confirmation row in `payment_confirmations` is written in the **same transaction** that marks the trip `Paid` (SCRUM-103), keyed by trip. A payment is decided `Paid` only while the trip is unpaid under its row lock, so PayHere redeliveries, retries after a crash, or a second PayHere payment can never create another confirmation. The confirmed amount is the verified amount, which must equal the final fare. Only the masked card's last four digits and brand are exposed.

After showing it, the app calls `POST /payments/{tripId}/confirmation/acknowledge` with the `confirmationId`. The first call records `acknowledgedAt`; repeats keep the first time, so the success screen is shown once even across devices.

Errors: `INVALID_REQUEST` (400, invalid trip ID, unknown fields or malformed JSON), `INVALID_CONFIRMATION_ID` (400), `AUTHENTICATION_REQUIRED` (401), `PAYMENT_FORBIDDEN` (403, another rider's trip), `TRIP_NOT_COMPLETED`, `PAYMENT_NOT_CONFIRMED` and `CONFIRMATION_MISMATCH` (409), and `IDENTITY_UNAVAILABLE`/`PAYMENT_STORE_UNAVAILABLE` (503).

The development page shows the same confirmation after **Simulate successful payment**, with a **Got it** button that acknowledges it.

## Kafka

Enable `Kafka__Enabled=true` and set `Kafka__BootstrapServers` to consume `goride.trip.events` using the independent `goride-payment` consumer group. Event envelopes must match the example. Broker ACLs should permit only trusted trip/fare producers to write completion events. The internal HTTP endpoint and Kafka use the same transaction logic.

Offsets are committed only after database commit. Temporary failures retry at the same offset. Invalid/conflicting events stop the service without committing the offset; inspect the logged topic/partition/offset and correct or reconcile the event before restarting. This avoids silently losing payment events. Optional broker settings are `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, and `Kafka__SaslPassword`.

## Verification

```sh
dotnet restore GoRide.Payment.slnx
dotnet build GoRide.Payment.slnx --configuration Release --no-restore
dotnet test GoRide.Payment.slnx --configuration Release --no-build
```

Tests cover authorization, validation, exact amounts, concurrent checkout, order reuse across restarts, fare corrections, unsafe configuration, settlement guards, PayHere signature formulas and development-page isolation. HTTP tests exercise the real application and MySQL data access with a deterministic identity stub. They do not need real PayHere credentials. Dedicated verification tests (SCRUM-668–671) belong to QA.

Set `PAYMENT_TEST_MYSQL` to a **disposable local/CI MySQL administrator connection** before running the HTTP/database tests. Each test creates and removes its own randomly named `payment_test_...` database; it does not use `payment_db`. If this variable is absent, database tests are explicitly skipped. The GitHub Actions workflow always supplies a fresh MySQL service and runs build, all tests, artifact upload and Docker build on every branch push and PR.

Example PowerShell for a disposable server:

```powershell
$env:PAYMENT_TEST_MYSQL = 'Server=127.0.0.1;Port=3306;User ID=root;Password=local-test-password;SslMode=Disabled'
dotnet test GoRide.Payment.slnx --configuration Release --logger trx
```

Story coverage: SCRUM-651–655 (card selection), SCRUM-656–663 (checkout, now on the PayHere sandbox), SCRUM-665–667 (SCRUM-103 dev: verification endpoint and business logic, validation and structured errors, parameterised ADO.NET data access) and SCRUM-674–675 (SCRUM-104 dev: confirmation endpoints and business logic, validation and structured errors). QA (SCRUM-668–671, 676–677) and CI/staging (SCRUM-672–673, 678) follow; pushing these branches does not perform a staging deployment.
