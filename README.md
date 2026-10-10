# GoRide Payment

SCRUM-101–105: a rider can manually save one test card and pay in-app after their trip completes, or use the **PayHere sandbox** checkout. Successful card payment commits the paid state, an in-app confirmation and an email receipt outbox entry together. Brevo sends receipts; local Log previews report `Logged`, never `Sent`. This SCRUM-101 follow-up covers saved cards and in-app card payment. Cash choice stays local to the frontend, and the driver never waits for payment. The service uses .NET 10, MySQL 8 and direct ADO.NET queries. PayHere checkout is fixed to `https://sandbox.payhere.lk`, so no live merchant key is needed or accepted.

## Run locally

1. Create a `payment_db` MySQL database and a service user with SELECT, INSERT and UPDATE access to its tables, plus DELETE on `payment_cards` for saved-card removal.
2. Apply `src/GoRide.Payment/Data/schema.sql` to that database using a schema administrator. It is the single schema source for fresh databases and upgrades: missing tables and their indexes are created, and the early SCRUM-104 `confirmation_id CHAR(36)` column is guardedly converted to `VARCHAR(36)` without changing IDs or removing data. Reapplying it preserves existing rows. It does not repair arbitrary schema drift. The application never creates or changes production schemas automatically.
3. Set `ConnectionStrings__Payments`, `Identity__BaseUrl`, and `InternalServices__ApiKey` in the environment. `.env.example` lists the settings; `.env` is not loaded automatically. Alternatively use a gitignored `src/GoRide.Payment/appsettings.Development.json`.
4. Run `dotnet run --project src/GoRide.Payment --urls http://localhost:8083`.
5. `GET /health` checks the database and all eleven application tables: `payments`, `payment_checkouts`, `payment_verifications`, `payment_confirmations`, `payment_contacts`, `payment_receipts`, `processed_payment_events`, `payment_cards`, `payment_card_requests`, `payment_card_attempts` and `driver_payment_notifications`.

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
| GET | `/payments/{tripId}/receipt` | Whether the email receipt was sent (address masked) and whether it can be resent |
| POST | `/payments/{tripId}/receipt/resend` | Accepts `{}` or no body; queues the receipt again (202) |
| GET | `/payments/cards` | Rider: `{ "cards": [...] }`, empty or one saved test card |
| POST | `/payments/cards` | Rider: `{ "number": "...", "expMonth": 12, "expYear": 2030, "cvc": "...", "holderName": "...", "makeDefault": true }`; last two fields optional; returns the saved card (201), or `CARD_LIMIT_REACHED` (409) |
| DELETE | `/payments/cards/{cardId}` | Rider: no body; removes their card (204) |
| POST | `/payments/cards/{cardId}/default` | Rider: `{}` or no body; returns their saved card with `isDefault: true` |
| POST | `/payments/{tripId}/pay` | Accepts exactly `{ "cardId": "...", "requestId": "nonempty-UUID" }`; charges the saved demo card, with one automatic retry for transient failures |
| GET | `/payments/{tripId}/status` | Payment status for the trip's rider and driver, or JSON `null` before completion arrives |
| GET | `/payments/driver/notifications?since=<ISO timestamp>&after=<cursor>` | Authenticated trip driver: recent card-payment-success feed, `{ notifications, nextCursor }` |
| GET | `/payments/driver/notifications/{tripId}` | Authenticated trip driver: one notification; 404 for absent or other drivers' trips |
| POST | `/payments/demo-completions` | Rider, Development with `DemoTrips__Enabled=true` only: `{ "tripId": "trp_...", "finalFare": 640.00 }`; returns the payment record |
| POST | `/internal/trip-events` | Trusted `TRIP_COMPLETED` ingestion with `X-Internal-Api-Key` |

All public payment operations require the rider's identity session and ownership, except `/status`, which also permits the assigned driver, and `/payments/driver/notifications`, which reads only the authenticated trip driver's notifications. PayHere notify instead verifies the provider signature; internal ingestion requires the internal service key. `GET /health` is anonymous and returns `{ "status": "healthy", "database": "connected" }`.

## Driver card-payment notifications (SCRUM-106)

The transaction that marks a card payment `Paid` also inserts `driver_payment_notifications`, alongside its confirmation and receipt. Unique trip and payment indexes enforce one snapshot under duplicate/concurrent provider notices and saved-card requests. The amount is the payment record's final fare in minor units, never its estimate. Paid time comes from that record; currency is the service's fixed LKR currency. Cash, pending, failed and amount-mismatched outcomes do not enqueue. Existing paid trips are not backfilled. Card charging/attempt logic is unchanged.

Apply `src/GoRide.Payment/Data/schema.sql` to the selected `payment_db` before deploying. The new InnoDB table uses `CREATE TABLE IF NOT EXISTS`, with unique trip/payment/event IDs, the authenticated driver ID, final amount/currency, brand/last4, paid time, and lease/retry fields. Reapplying preserves rows and delivery state. The table is required by `/health`.

Both driver GETs return `tripId`, `amount` (decimal major units), `currency`, `cardBrand`, `cardLast4`, `paidAt`. The card fields are already available to the driver through `/status`; no additional card details or rider identity are exposed. Responses use `Cache-Control: no-store`. The feed filters by the identity session's `sub`; no caller-selected driver ID is accepted. A signed-in user with no matching trips receives an empty feed. The per-trip endpoint returns `404 NOTIFICATION_NOT_FOUND` for missing or unowned notifications.

`since` is inclusive and defaults to seven days ago. Supply ISO 8601 with seconds and `Z` or an explicit timezone offset (URL-encode `+`); future, blank or malformed values return `400 INVALID_SINCE`. `after` is an optional non-negative integer; malformed values return `400 INVALID_CURSOR`. Unknown/repeated parameters return `400 INVALID_REQUEST`. Pages contain at most 100 rows, ordered by an internal sequence, with `nextCursor` or null. Keep the same `since` while following `nextCursor` via `after`; restart from the first page on the next polling round so late commits are not missed. No session returns `401 AUTHENTICATION_REQUIRED`; store/identity failures retain the existing structured 503 errors.

The dispatcher uses two-minute leases, token-guarded completion, a 15-second HTTP timeout and up to five attempts with 30s/2m/10m/30m backoff. HTTP 408/429/5xx, transport errors and timeouts retry; other HTTP errors and invalid configuration fail permanently. `Pending`, `Sending`, `Retry`, `Failed`, `Logged` and `Accepted` describe delivery state. An unconfigured URL yields `Logged` with no `accepted_at`. A 2xx receiver response yields `Accepted`, which means service acceptance, not proven FCM device delivery. The polling feed remains available in every delivery state.

| Environment variable | Default / purpose |
| --- | --- |
| `Notification__BaseUrl` | Empty: log only. Configure the notification service origin when an appropriate receiver is deployed. |
| `Notification__DriverPaymentPath` | No default; required with BaseUrl. Absolute path on that same origin, for example `/api/notifications/driver/card-payment-succeeded`. |
| `Notification__ApiKey` | Optional secret supplied externally; sent as `X-Internal-Api-Key`. |
| `Notification__DispatcherEnabled` | `true`; `false` pauses dispatch while preserving the feed and queued rows. |
| `Notification__PollSeconds` | `5`, clamped to 1–300 seconds. |

**Notification-service integration still needed:** the reference service's `/api/notifications/trip/payment-confirmed` targets riders. Its driver `booking-change` endpoint labels messages as booking changes, does not deduplicate event IDs, and returns success even after channel failure. Neither provides an idempotent driver card-payment receiver; that repository is unchanged. The configurable adapter expects POST JSON `{ eventId, eventType: "DRIVER_CARD_PAYMENT_SUCCEEDED", paymentId, driverId, tripId, amount, currency, paidAt }`, plus `Idempotency-Key: <eventId>`. The receiver must durably accept/deduplicate that key before returning 2xx, authenticate the internal caller, and route the payment notice to the driver. Retries and expired leases reuse the same event ID. Exactly-once outbox creation and frontend trip dedupe are implemented; exactly-once downstream processing requires that receiver contract. No live push delivery has been verified. Logged/failed rows are terminal; enabling a URL affects future queued notifications, with any replay requiring an explicit operational decision.

Whitebox and MySQL tests in `DriverNotificationTests.cs` cover Paid snapshots, ignored states/mismatches, duplicate and concurrent processing, atomic rollback, schema reapplication, driver ownership, input errors, pagination, retry limits, stable delivery IDs, expired leases and honest logging. Tests use in-process HTTP stubs and disposable databases. Set `PAYMENT_TEST_MYSQL` to the local test connection and run `dotnet test GoRide.Payment.slnx -c Release --no-restore -m:1`. Windows test logging disables the system EventLog provider so tests do not require administrative privileges.

Saved-card responses contain `cardId`, `brand`, `last4`, `expMonth`, `expYear`, `holderName`, `isDefault` and `createdAt`. `/pay` returns `status: "Paid"`, `alreadyPaid` and `confirmation` (fields shown below); a successful retry returns the original confirmation. `/status` returns `tripId`, `status`, `method`, `amount`, `currency`, `paidAt`, `cardBrand` and `cardLast4`. Card numbers, CVCs, test behavior and fingerprints are never returned. There is no card catalogue endpoint, cash-selection endpoint or cash-confirmation endpoint in this branch.

The get/select paths match the frontend's existing `payments.get` and `payments.selectMethod` contract. The checkout endpoint returns JSON (`orderId`, `actionUrl`, `fields`, `amount`, `currency`); the client builds a hidden form from `fields` and POSTs it to `actionUrl`. Route these paths through the same frontend origin so its identity session cookie reaches the service. The payment service verifies the cookie against identity-auth's `/api/me`; it never accepts a browser-supplied rider ID. Its HTTP client does not store cookies or follow redirects. Configure `Identity__BaseUrl` to the actual identity service. Direct cross-origin clients additionally require an allowed CORS origin and credentials.

The trip/fare producer must publish completion with the authoritative final fare. Existing frontend mock payments are not authoritative input. For real trips, no browser can register a completion event or set an amount; the Development-only simulated-ride endpoint is described below. Integration with the running trip producer/gateway and deployment are separate from this repository's service implementation.

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

## SCRUM-105: email payment receipt

After a card payment is verified, the rider gets an email receipt with the verified amount, the fare breakdown, the card brand and last four digits, the PayHere reference, the trip reference, a receipt number and the payment time in Sri Lanka time.

**One receipt per payment.** The receipt row in `payment_receipts` is written in the **same transaction** that marks the trip `Paid` (SCRUM-103), keyed by trip, so PayHere redeliveries, retries after a crash or a second payment never create a second receipt. The amount is read from the verified confirmation, so the receipt always matches the true final amount.

**Address.** At checkout the rider's email from the verified identity session is saved in `payment_contacts`. PayHere's placeholder contact details are never stored or emailed. A rider without a deliverable email gets a `NoEmail` receipt instead of a send.

**Delivery.** `payment_receipts` is an outbox. A background sender claims one due row at a time with a lease (`UPDATE ... LIMIT 1`, so several instances never take the same row), sends it and records the provider message ID. Transient failures retry after 30 s, 2 min, 10 min and 30 min. After 5 attempts, or when the provider permanently rejects the message, the receipt is `Failed`. A sender that crashes mid-send leaves an expired lease (2 min), and the row is claimed again.

Statuses: `Pending`, `Sending`, `Retry`, `Sent`, `Logged`, `Failed`, `NoEmail`. `Sent` means the email provider accepted the message; it does not guarantee inbox delivery. `Logged` means a local preview only, with no email sent and no `sentAt`. Provider retries after an ambiguous network failure can redeliver an email; a unique receipt row is not an exactly-once email guarantee.

```json
{
  "receiptId": "0b9a3e55-6f5e-4c55-9d0a-5d1f2c7c9a10",
  "status": "Sent",
  "recipient": "r***e@gmail.com",
  "sentAt": "2026-10-09T10:00:04+00:00",
  "attempts": 1,
  "canResend": false,
  "resendAvailableAt": "2026-10-09T10:01:04+00:00",
  "resendsLeft": 3
}
```

**Resend.** `POST /payments/{tripId}/receipt/resend` queues the same receipt again for the address captured at checkout. The request takes no fields, so a caller cannot send a receipt to another address. A resend is allowed once the last send has finished, 60 seconds after the last send or request, and at most 3 times per trip. The database update repeats these checks, so concurrent requests queue only one send.

Errors: `INVALID_REQUEST` (400, invalid trip ID, unknown fields or malformed JSON), `AUTHENTICATION_REQUIRED` (401), `PAYMENT_FORBIDDEN` (403), `TRIP_NOT_COMPLETED`, `RECEIPT_NOT_AVAILABLE` (not paid yet), `RECEIPT_EMAIL_MISSING` and `RECEIPT_IN_PROGRESS` (409), `RECEIPT_RESEND_TOO_SOON` and `RECEIPT_RESEND_LIMIT` (429), and `IDENTITY_UNAVAILABLE`/`PAYMENT_STORE_UNAVAILABLE` (503). `RECEIPT_RESEND_TOO_SOON` includes a `Retry-After` header and `retryAfterSeconds` in the body.

### Sending real email with Brevo (free)

By default `Email__Provider=Brevo`: receipt emails require a configured key and verified sender. Missing configuration fails delivery with retries; it never reports a successful email. Set `Email__Provider=Log` explicitly for local previews, which report `Logged`. To configure delivery:

1. Create a free account at [brevo.com](https://www.brevo.com) (300 emails a day).
2. Under **Senders, Domains & Dedicated IPs**, add and verify the address receipts are sent from.
3. Under **SMTP & API → API Keys**, create an API key.
4. Set `Email__Provider=Brevo`, `Email__FromAddress` (the verified sender), optionally `Email__FromName`, and `Email__Brevo__ApiKey`, in the environment or the gitignored `appsettings.Development.json`. Never commit the key.

Receipts are sent through Brevo's transactional API (`POST https://api.brevo.com/v3/smtp/email`) with an HTML body and a plain-text alternative. Brevo 4xx responses other than 401, 403 and 429 are treated as permanent failures; 401/403 (key or IP authorization), 429, 5xx, timeouts and network errors are retried. `Receipts__DispatcherEnabled` (default `true`) and `Receipts__PollSeconds` (default 5) control the background sender.

The development page has an optional **Receipt email** field on the test ride. After **Simulate successful payment** it shows the receipt status, a **Preview email** link with the exact email, and **Resend**.

## One saved test card

The rider manually adds a card in **Profile → Payment methods**, or saves it during checkout. There is no card catalogue or automatic card seeding. Each account can save one card; it is selected automatically for future payments. To replace it, remove the existing card, then add the replacement. Existing accounts with multiple cards from an older version can remove them without losing payment history.

For a successful test payment, manually enter `4242 4242 4242 4242`, any future expiry, and a three-digit CVC. This is a simulation: no real funds move. The backend retains fault-injection numbers for automated decline tests, but they are not advertised by an app endpoint or UI.

Validation returns field errors for an invalid number, expiry, CVC, or holder name. `CARD_LIMIT_REACHED` (409) means a card is already saved. A database advisory lock serializes saves and removals for each rider, including simultaneous first-card saves. Only masked metadata and a test fingerprint are stored; full card numbers and CVCs are discarded.

`POST /payments/{tripId}/pay` charges a saved card after a short simulated processing delay (`DemoCard__ProcessingMilliseconds`, default 1500). The charge is recorded as a `DemoCard` provider result through the same verification path as a PayHere notice. Receipts for demo payments say "Payment reference" instead of "PayHere reference".

### SCRUM-107: one automatic retry

Send `{ "cardId": "saved-card-UUID", "requestId": "new-nonempty-UUID" }`. Keep the same request ID when a response is lost; after a confirmed failure, a manual Retry uses a new ID and may select the same or a replacement saved card. Reusing an ID with another card returns 409 `PAYMENT_REQUEST_CONFLICT`. Missing/malformed request IDs and caller-supplied amounts, currency or identity return 400 `INVALID_REQUEST`; missing/foreign cards return 404 `CARD_NOT_FOUND`. Ownership and completed-trip validation still apply. An already Paid trip returns 200 with its original confirmation, including after card removal, without charging again.

Only `PROCESSING_ERROR`, `PROVIDER_TIMEOUT` and `PROVIDER_UNAVAILABLE` receive one automatic server retry within the same pay request. Backoff defaults to 1000 ms (`DemoCard__RetryMilliseconds`, clamped to 0–2000 for local tests). Hard declines (`CARD_DECLINED`, `INSUFFICIENT_FUNDS`, `EXPIRED_CARD`, `INCORRECT_CVC`) are final immediately; invalid card numbers are rejected when saving a card. The service performs no real card-provider network call: timeout and 5xx behaviours are deterministic demo outcomes.

Successful responses add `attempts` (for this request) and `autoRetried`. Final declines return 402 ProblemDetails with `code`, `title`, `retryable`, `autoRetried` and `attempts`. For example, two processing failures return `PROCESSING_ERROR`, `retryable: true`, `autoRetried: true`, `attempts: 2`. Hard declines return `retryable: false`, `autoRetried: false`, `attempts: 1`; use another card or correct the card issue. The logical request becomes `Failed`; the unpaid payment stays `Pending` to allow manual retry. No cash fallback or automatic card disabling is implemented.

The status endpoint adds `attemptCount` (all attempts for the trip), `lastFailureCode` (retained after a later success), `requestId`, `requestState` (`Processing`, `Retrying`, `Failed`, `Paid`, or null), `requestAttempts` (completed attempts in the latest request), `autoRetried` and `retryable`. Polling `Retrying` lets checkout show “Payment didn't go through, trying once more...” while the original POST is still running. Card data on status remains limited to brand/last4.

Apply `schema.sql` before deploying. It adds `payment_card_requests` and `payment_card_attempts` with `CREATE TABLE IF NOT EXISTS`; existing tables need no ALTER. Requests retain only a safe demo-card snapshot and the server's final fare in minor units. Attempts record trip-wide sequence 1..n, request attempt 1..2, outcome, failure code, amount/currency, start/completion times and whether automatic. The existing payment `cardAttemptCount` now counts every started in-app attempt. A request and checkout are created together; verification, attempt completion, request result, Paid state, confirmation and receipt commit together. The database trip lease serializes multiple service instances and fare updates. Interrupted attempts resume with their original deterministic provider identity; a disconnected browser does not cancel an accepted charge. Another request receives `PAYMENT_IN_PROGRESS` until an interrupted request is resumed. Fare corrections also wait for an unfinished request; a completed failure permits a later request at the corrected final fare. Paid fares remain immutable. A future real provider adapter must preserve provider idempotency and reconcile ambiguous timeouts before starting another charge.

Demo cards (future expiry, any three-digit CVC):

| Number | Result |
| --- | --- |
| `4242424242424242` | Success on first attempt |
| `4000000000000341` | GoRide demo: processing error, then automatic success |
| `4000000000000119` | Processing error twice |
| `4000000000000077` | GoRide demo: provider timeout twice |
| `4000000000000085` | GoRide demo: provider 503 twice |
| `4000000000000002` | Card declined, one attempt |
| `4000000000009995` | Insufficient funds, one attempt |
| `4000000000000069` | Expired card, one attempt |
| `4000000000000127` | Incorrect CVC, one attempt |

Cash choice is local to the frontend and makes no payment-service request. This SCRUM-101 branch provides saved cards and in-app card payment; cash selection and driver confirmation belong to SCRUM-109, SCRUM-112 and SCRUM-113. The driver never waits for payment. `GET /payments/{tripId}/status` is shared by the rider and the driver; `select-method` accepts only Card.

Simulated rides (the app's demo drivers) never reach the trip service, so in Development with `DemoTrips__Enabled=true` the rider app may report one with `POST /payments/demo-completions` `{ "tripId": "trp_...", "finalFare": 640.00 }`. It only accepts simulated `trp_` IDs, is idempotent, and is 404 everywhere else. Real trips are completed only by the trip service through `/internal/trip-events`.

## Kafka

Enable `Kafka__Enabled=true` and set `Kafka__BootstrapServers` to consume `goride.trip.events` using the independent `goride-payment` consumer group. Event envelopes must match the example. Broker ACLs should permit only trusted trip/fare producers to write completion events. The internal HTTP endpoint and Kafka use the same transaction logic.

Offsets are committed only after database commit. Temporary failures retry at the same offset. Invalid/conflicting events stop the service without committing the offset; inspect the logged topic/partition/offset and correct or reconcile the event before restarting. This avoids silently losing payment events. Optional broker settings are `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, and `Kafka__SaslPassword`.

## Verification

```sh
dotnet restore GoRide.Payment.slnx
dotnet build GoRide.Payment.slnx --configuration Release --no-restore
dotnet format GoRide.Payment.slnx --verify-no-changes --no-restore
dotnet test GoRide.Payment.slnx --configuration Release --no-build
```

Tests cover authorization, validation, exact amounts, concurrent checkout, order reuse across restarts, fare corrections, unsafe configuration, settlement guards, PayHere signature formulas, development-page isolation and receipts (one per paid trip under redelivery, retry backoff, lease recovery, resend limits, email validation and rendering). HTTP tests exercise the real application and MySQL data access with a deterministic identity stub. They do not need real PayHere credentials. Dedicated verification tests (SCRUM-668–671) belong to QA.

Set `PAYMENT_TEST_MYSQL` to a **disposable local/CI MySQL administrator connection** before running the HTTP/database tests. Each test creates and removes its own randomly named `payment_test_...` database; it does not use `payment_db`. If this variable is absent, database tests are explicitly skipped. Tests also cover schema upgrades/reapplication with a paid trip and recovery of `/health` after applying the schema.

Both solution files include the application and tests; always name the solution to avoid MSB1011. CI uses `GoRide.Payment.slnx` on .NET 10, supplies MySQL 8.0 at port 3306 through `PAYMENT_TEST_MYSQL`, and runs on PRs into `dev`/`main`, pushes to those branches, and manual dispatch. It restores, builds Release, runs tests, uploads results, checks formatting, audits vulnerable/deprecated dependencies and builds the Docker image. Formatting and deprecated dependencies are advisory; vulnerable dependencies, build/tests and Docker are blocking through `CI / CI Gate`. Compiler warnings are reported, not treated as errors.

Example PowerShell for a disposable server:

```powershell
$env:PAYMENT_TEST_MYSQL = 'Server=127.0.0.1;Port=33307;User ID=root;SslMode=Disabled'
dotnet test GoRide.Payment.slnx --configuration Release --logger trx
```

## Ride and payment contract

- The trip service owns completion and the final fare. A browser cannot choose the amount for a real trip.
- Payment owns card settlement. Completing a ride, selecting Card, and paying are separate actions. Only a verified card success settles card payment. The driver never waits for payment.
- Card payment, its confirmation, and its receipt outbox row commit together. Repeated Pay requests return the existing confirmation under the trip lock.
- The rider polls payment status while card checkout is open. A refresh or lost Pay response reconciles with the server before showing the outcome. Cash choice stays local to the frontend.
- The identity service supplies the receipt address from the registered account. Neither the payment form nor the resend endpoint accepts an alternate address.
- Email delivery happens independently after successful payment. An email failure does not undo payment or charge the rider again; the receipt can be retried with cooldown and limits.

The current trip-to-payment completion notifier still uses an in-memory queue with bounded retries. A durable trip outbox is needed before production so restarts or a long payment-service outage cannot lose completion notifications. The current card provider is deliberately test-only; a real card-on-file integration must use provider tokenization before accepting real cards.
On a restricted Windows account, Event Log writes may fail during HTTP tests; set `$env:Logging__EventLog__LogLevel__Default = 'None'` for that test process. If MSBuild worker pipes are denied, add `-m:1 -p:UseSharedCompilation=false` to build and `-m:1` to restore/test. These local restrictions do not require changes to the Ubuntu CI workflow. The full solution format check also needs access to its build-host pipe; a folder whitespace check alone is not equivalent.

Story coverage: SCRUM-651–655 (card selection), SCRUM-656–663 (checkout, now on the PayHere sandbox), SCRUM-665–667 (SCRUM-103 dev: verification endpoint and business logic, validation and structured errors, parameterised ADO.NET data access) and SCRUM-674–675 (SCRUM-104 dev: confirmation endpoints and business logic, validation and structured errors). QA (SCRUM-668–671, 676–677) and CI/staging (SCRUM-672–673, 678) follow. Feature-branch pushes do not deploy; pushes to `dev` deploy when `CD_ENABLED=true`.

## Azure deployment

`cd.yml` runs the reusable CI, builds the root Dockerfile, pushes `ghcr.io/goride-app/goride-payment:<commit SHA>` and `:dev`, then updates the Container App to the immutable SHA tag. The runtime image uses .NET 10, a non-root user and port 8080. CD never applies SQL, configures ingress, sets runtime environment variables, or creates/updates Container App secrets. It preserves the existing configuration. It checks that Brevo, a sender address, an API-key secret reference and receipt dispatch are configured before swapping the image. Provision those settings once using the steps below; no new GitHub application secrets are needed.

GitHub repository variables: `CD_ENABLED=true`, `AZURE_RESOURCE_GROUP`, `AZURE_CONTAINERAPP_NAME=goride-payment`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. `GITHUB_TOKEN` is the built-in secret used to push GHCR; Azure login uses OIDC and needs the repository/ref federated identity and permission to update the app. The app must already be able to pull the GHCR image (public package or configured registry credentials). Ingress must target port 8080. Use single-revision mode: the health check calls the app FQDN and does not prove which revision served it if traffic is split across revisions.

### Container configuration

Use normal environment variables for non-secrets and `secretref:<name>` for secrets. These names come from the application's configuration reads; `ConnectionStrings__Payment` (singular) is not used.

| Environment variable | Secret? | Required value / behavior when omitted |
| --- | --- | --- |
| `ConnectionStrings__Payments` | Yes | MySQL connection to `goride-dbv2.mysql.database.azure.com:3306`, database `payment_db`, with the service username, password and `SslMode=VerifyFull`. Absent: HTTP 500 `INTERNAL_ERROR`; wrong host/database/credentials, TLS, permissions or network: normally HTTP 503 `PAYMENT_STORE_UNAVAILABLE`. |
| `InternalServices__ApiKey` | Yes | Shared with trusted trip producers. Missing: internal ingestion returns 401 `INVALID_SERVICE_KEY`. |
| `Identity__BaseUrl` | No | Reachable HTTPS identity service. Defaults to `https://localhost:7136`; authenticated calls normally fail with 503 `IDENTITY_UNAVAILABLE` in Azure. |
| `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, etc. | No | Actual frontend origins for credentialed cross-origin requests. The first default is `http://localhost:3000`; unlisted browser origins are blocked. Same-origin proxying does not need CORS. |
| `PayHere__MerchantId` | No | Sandbox merchant ID. Missing/invalid: 503 `PAYHERE_NOT_CONFIGURED`. |
| `PayHere__MerchantSecret` | Yes | Sandbox merchant secret. Missing/invalid: 503 `PAYHERE_NOT_CONFIGURED`. |
| `PayHere__ReturnUrl`, `PayHere__CancelUrl`, `PayHere__NotifyUrl` | No | HTTPS frontend return/cancel URLs and public payment-service `/payments/payhere/notify` URL. Checked-in HTTP localhost defaults fail validation in Production: 503 `CHECKOUT_NOT_CONFIGURED`. |
| `Email__Provider` | No | Defaults to `Brevo`. Set `Log` explicitly for local previews (`Logged`, no `sentAt`); other values are invalid. |
| `Email__FromAddress` | No | Verified Brevo sender. Missing/invalid with Brevo: retries, then `Failed` after 5 attempts. |
| `Email__FromName` | No | Optional; defaults to `GoRide`. |
| `Email__Brevo__ApiKey` | Yes | Brevo API key. Missing: retries, then `Failed` after 5 attempts. |
| `Receipts__DispatcherEnabled` | No | Defaults to `true`. `false` leaves receipts queued. |
| `Receipts__PollSeconds` | No | Defaults to 5, clamped to 1–300 seconds. Retry and resend limits are fixed in code. |
| `Kafka__Enabled` | No | Defaults to `false`; no Kafka ingestion unless enabled. HTTP ingestion remains available. |
| `Kafka__BootstrapServers` | No | Required if Kafka is enabled; missing stops the background service/host. |
| `Kafka__TripEventsTopic`, `Kafka__GroupId` | No | Defaults: `goride.trip.events`, `goride-payment`. |
| `Kafka__SecurityProtocol`, `Kafka__SaslMechanism` | No | Broker-dependent; omitted values use client defaults. Use the names accepted by the Confluent enums. Wrong security settings can prevent consumption; invalid names throw. |
| `Kafka__SaslUsername`, `Kafka__SaslPassword` | Yes | Required when the broker uses SASL credentials; absent/wrong credentials prevent consumption. |
| `DevelopmentCheckout__Enabled` | No | Defaults to `false`; leave false in Azure. Routes also require Development and loopback access. |
| `DemoTrips__Enabled` | No | Defaults to `false`; leave false in Azure. Simulated completion also requires Development. |
| `DemoCard__ProcessingMilliseconds` | No | Defaults to 1500; simulated card processing delay, capped at 10000 ms. |
| `DemoCard__RetryMilliseconds` | No | Defaults to 1000; automatic retry backoff, clamped to 0–2000 ms. |
| `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` | No | Defaults to Production. Keep Production in Azure; `DOTNET_ENVIRONMENT` takes precedence for this hosting model. |
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_URLS` | No | The runtime image defaults to port 8080. A URL override takes precedence; it must match the ingress target port. |
| `AllowedHosts` | No | Defaults to `*`; if restricted, include the app/gateway host or requests are rejected. |
| `Logging__LogLevel__Default`, `Logging__LogLevel__Microsoft.AspNetCore` | No | Defaults to Information and Warning; optional logging overrides. |

`/health` opens `ConnectionStrings__Payments` and queries **`payments`, `payment_checkouts`, `payment_verifications`, `payment_confirmations`, `payment_contacts`, `payment_receipts`, `processed_payment_events`, `payment_cards`**. Empty tables are healthy. Health does not validate every column/index, write permissions, Identity, PayHere, Kafka or Brevo.

For the reported 503, unapplied schema is the leading suspect, especially a missing `payment_receipts` table after this upgrade. The same code also covers wrong database/credentials, insufficient SELECT grants and MySQL network/TLS failures. A completely absent connection key instead produces 500. Check the Container App logs for the underlying MySQL error without sharing credentials.

### Apply to goride-dbv2 / payment_db

Run these commands yourself from a Bash shell with Azure/MySQL access and this repository as the working directory. Do not run the test suite against Azure. Use an existing trusted CA bundle and a machine allowed by the server's firewall/private network; the Container App must also have DNS and TCP 3306 access. No broad firewall change is needed or made by these commands.

1. Sign in to the correct Azure subscription, select the resource group and pause receipt dispatch before repairing the schema. This keeps queued receipts untouched while delivery settings are configured.

```bash
read -rp 'Azure resource group: ' RG
APP=goride-payment
az containerapp revision set-mode --name "$APP" --resource-group "$RG" --mode single -o none
az containerapp update --name "$APP" --resource-group "$RG" \
  --set-env-vars Receipts__DispatcherEnabled=false -o none
az containerapp secret list --name "$APP" --resource-group "$RG" --query '[].name' -o tsv
```

2. Choose the existing secret names (or new names if none exist). Enter secrets at hidden prompts; no secret belongs in a file, command history or GitHub variable. The connection string must contain the server, port, database, service user/password and TLS setting listed above. The service account needs SELECT, INSERT and UPDATE on `payment_db.*`, plus DELETE on `payment_cards`; use a separate schema administrator for DDL.

```bash
read -rp 'Payment connection secret name: ' PAYMENT_DB_SECRET
read -rp 'Brevo API key secret name: ' BREVO_SECRET
read -rsp 'Full Payments connection string: ' PAYMENT_CONNECTION_STRING
printf '\n'
read -rsp 'Brevo API key: ' BREVO_API_KEY
printf '\n'
read -rp 'Verified Brevo sender address: ' RECEIPT_FROM
az containerapp secret set --name "$APP" --resource-group "$RG" \
  --secrets "$PAYMENT_DB_SECRET=$PAYMENT_CONNECTION_STRING" "$BREVO_SECRET=$BREVO_API_KEY" -o none
unset PAYMENT_CONNECTION_STRING BREVO_API_KEY
az containerapp update --name "$APP" --resource-group "$RG" --set-env-vars \
  "ConnectionStrings__Payments=secretref:$PAYMENT_DB_SECRET" \
  "Email__Brevo__ApiKey=secretref:$BREVO_SECRET" \
  Email__Provider=Brevo "Email__FromAddress=$RECEIPT_FROM" Email__FromName=GoRide \
  Receipts__DispatcherEnabled=false Receipts__PollSeconds=5 -o none
```

3. Apply the canonical SQL using MySQL 8.0. `--password` prompts without storing the password. `source` executes `schema.sql` directly; do not maintain a separate migration copy. Run one schema deployment at a time. The guarded CHAR-to-VARCHAR upgrade may take a table lock, so use a suitable maintenance window for a large existing table.

```bash
read -rp 'MySQL schema administrator username: ' MYSQL_ADMIN
read -rp 'Absolute path to trusted MySQL CA bundle: ' MYSQL_SSL_CA
mysql --host=goride-dbv2.mysql.database.azure.com --port=3306 \
  --user="$MYSQL_ADMIN" --password --ssl-mode=VERIFY_IDENTITY --ssl-ca="$MYSQL_SSL_CA" \
  --execute='CREATE DATABASE IF NOT EXISTS payment_db;'
mysql --host=goride-dbv2.mysql.database.azure.com --port=3306 \
  --user="$MYSQL_ADMIN" --password --ssl-mode=VERIFY_IDENTITY --ssl-ca="$MYSQL_SSL_CA" \
  --database=payment_db --execute='source src/GoRide.Payment/Data/schema.sql'
```

If the existing service account lacks table grants, run this as the schema administrator, replacing the account and host with the existing MySQL principal. No account or password change is necessary:

```sql
GRANT SELECT, INSERT, UPDATE ON payment_db.* TO '<existing-service-user>'@'<existing-account-host>';
GRANT DELETE ON payment_db.payment_cards TO '<existing-service-user>'@'<existing-account-host>';
```

4. From the same network, connect as the **service user**, using the same TLS options, and execute the actual health query to confirm its database and SELECT grants:

```sql
USE payment_db;
SHOW TABLES;
SELECT 1 FROM payments
LEFT JOIN payment_checkouts ON payments.trip_id = payment_checkouts.trip_id
LEFT JOIN payment_verifications ON payments.trip_id = payment_verifications.trip_id
LEFT JOIN payment_confirmations ON payments.trip_id = payment_confirmations.trip_id
LEFT JOIN payment_contacts ON payments.trip_id = payment_contacts.trip_id
LEFT JOIN payment_receipts ON payments.trip_id = payment_receipts.trip_id
LEFT JOIN processed_payment_events ON payments.trip_id = processed_payment_events.trip_id
LEFT JOIN payment_cards ON FALSE
LEFT JOIN payment_card_requests ON FALSE
LEFT JOIN payment_card_attempts ON FALSE LIMIT 1;
```

Zero rows is fine; a SQL error is not. There should be ten application tables. Then check health and enable delivery after confirming the sender/key setup. Updating environment variables creates a new revision; if only a secret's value was changed, restart the consuming revision or deploy a new one so it takes effect.

```bash
FQDN=$(az containerapp show --name "$APP" --resource-group "$RG" \
  --query properties.configuration.ingress.fqdn -o tsv)
curl --fail-with-body "https://$FQDN/health"
az containerapp update --name "$APP" --resource-group "$RG" \
  --set-env-vars Receipts__DispatcherEnabled=true -o none
curl --fail-with-body "https://$FQDN/health"
```

Expected: HTTP 200 `{"status":"healthy","database":"connected"}`. A remaining 503 requires fixing the underlying MySQL connection, grants or network/TLS error; Brevo settings do not affect `/health`. Configure the Identity, internal service, CORS and PayHere settings from the table before exercising payment flows. Existing receipts stored as `Sent` by an older Log provider are exposed as `Logged` with no `sentAt`. They are not automatically emailed; use the authenticated resend endpoint subject to its normal limits.
