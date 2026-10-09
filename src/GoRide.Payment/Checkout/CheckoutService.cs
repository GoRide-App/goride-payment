using GoRide.Payment.Data;
using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class CheckoutService(PaymentStore payments, CheckoutStore checkouts,
    ICheckoutProvider provider, StripeSettings settings, TimeProvider clock)
{
    public async Task<CheckoutRedirect> CreateAsync(string tripId, string riderId, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        await using var tripLock = await checkouts.LockAsync(tripId, ct);
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        PaymentRules.SelectCard(payment, riderId); // Ownership and payment-state guards.
        if (payment.Method != "Card")
            throw new PaymentException(409, "CARD_NOT_SELECTED", "Select card payment before opening checkout.");
        if (payment.FinalFare <= 0 || payment.FinalFare * 100 > 99999999)
            throw new PaymentException(409, "CHECKOUT_AMOUNT_UNSUPPORTED", "This fare cannot be paid by card through checkout.");
        settings.Validate();
        var amountMinor = decimal.ToInt64(payment.FinalFare * 100);
        var attempt = await checkouts.LatestAsync(tripId, ct);
        if (attempt is not null)
        {
            var existing = await ResolveAsync(attempt, ct);
            attempt = existing.Attempt;
            EnsureUnpaid(existing.Session);
            if (existing.Session.Status == "open")
            {
                if (attempt.AmountMinor == amountMinor) return Redirect(existing.Session);
                await ConfirmExpiredAsync(attempt, ct);
            }
        }
        var urls = settings.ReturnUrls(tripId);
        attempt = new(tripId, "goride-" + Guid.NewGuid().ToString("N"), amountMinor, "lkr", urls.Success, urls.Cancel, clock.GetUtcNow());
        await checkouts.InsertAsync(attempt, ct);
        var created = await ResolveAsync(attempt, ct);
        EnsureUnpaid(created.Session);
        if (created.Session.Status != "open")
            throw new PaymentException(409, "CHECKOUT_EXPIRED", "The checkout has expired. Retry to prepare another checkout.");
        return Redirect(created.Session);
    }

    // Called while holding the same trip lock as CreateAsync. If Stripe's outcome
    // is unknown or already complete, preserve the old fare for reconciliation.
    public async Task ExpireForFareChangeAsync(string tripId, CancellationToken ct)
    {
        var attempt = await checkouts.LatestAsync(tripId, ct);
        if (attempt is null) return;
        var existing = await ResolveAsync(attempt, ct);
        EnsureUnpaid(existing.Session);
        if (existing.Session.Status == "open") await ConfirmExpiredAsync(existing.Attempt, ct);
    }

    private async Task<(CheckoutAttempt Attempt, HostedSession Session)> ResolveAsync(CheckoutAttempt attempt, CancellationToken ct)
    {
        if (attempt.SessionId is null)
        {
            // Stripe may prune idempotency keys after 24h. Never recreate an unknown
            // result outside that window: a paid session might otherwise be duplicated.
            if (clock.GetUtcNow() - attempt.CreatedAt >= TimeSpan.FromHours(23))
                throw new PaymentException(409, "CHECKOUT_RECONCILIATION_REQUIRED", "The earlier checkout outcome must be checked before another can be created.");
            var created = await provider.CreateAsync(attempt, ct);
            StripeCheckoutClient.ValidateSession(created, attempt);
            await checkouts.SaveSessionAsync(attempt, created, ct);
            attempt = attempt with { SessionId = created.Id };
        }
        // Creation retries can return a cached response. Retrieve current state so
        // a session that completed since creation is never offered for another payment.
        var session = await provider.GetAsync(attempt, ct);
        StripeCheckoutClient.ValidateSession(session, attempt);
        return (attempt, session);
    }

    private async Task ConfirmExpiredAsync(CheckoutAttempt attempt, CancellationToken ct)
    {
        var expired = await provider.ExpireAsync(attempt, ct);
        StripeCheckoutClient.ValidateSession(expired, attempt);
        if (expired.Status != "expired" || expired.PaymentStatus != "unpaid")
            throw new PaymentException(409, "CHECKOUT_RECONCILIATION_REQUIRED", "The old checkout could not be safely expired before changing the fare.");
    }

    private static void EnsureUnpaid(HostedSession session)
    {
        if (session.Status == "complete" || session.PaymentStatus != "unpaid")
            throw new PaymentException(409, "CHECKOUT_AWAITING_VERIFICATION", "This checkout has completed. Payment verification is required before any further attempt.");
    }

    private CheckoutRedirect Redirect(HostedSession session)
    {
        if (session.ExpiresAt <= clock.GetUtcNow().ToUnixTimeSeconds())
            throw new PaymentException(409, "CHECKOUT_EXPIRED", "The checkout has expired. Retry shortly.");
        return new(session.Id, session.Url!, session.AmountTotal / 100m, session.Currency.ToUpperInvariant(), DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt));
    }
}
