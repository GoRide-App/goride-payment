using GoRide.Payment.Data;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;

namespace GoRide.Payment.Checkout;

public sealed class CheckoutService(PaymentStore payments, CheckoutStore checkouts, VerificationStore verifications,
    Receipts.ReceiptStore receipts, PayHereSettings settings, TimeProvider clock)
{
    public async Task<CheckoutForm> CreateAsync(string tripId, string riderId, RiderContact contact, CancellationToken ct)
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
        var urls = settings.Urls(tripId);
        var amountMinor = decimal.ToInt64(payment.FinalFare * 100);

        // Reuse the open order while the fare is unchanged, so a double click or a rider
        // returning from PayHere never creates a second payable order for the same trip.
        // A fare correction starts a new order; a late payment of the old order is caught
        // by verification because its amount no longer matches the final fare.
        var attempt = await checkouts.LatestAsync(tripId, ct);
        var outcomes = await verifications.OutcomesAsync(tripId, ct);
        if (outcomes.Any(o => o.Outcome is VerificationOutcome.AmountMismatch or VerificationOutcome.DuplicatePayment
            or VerificationOutcome.Chargedback))
            throw new PaymentException(409, "CHECKOUT_RECONCILIATION_REQUIRED", "A card payment for this trip needs review before another checkout.");
        if (attempt is not null && outcomes.LastOrDefault(o => o.OrderId == attempt.OrderId).Outcome == VerificationOutcome.Pending)
            throw new PaymentException(409, "CHECKOUT_AWAITING_VERIFICATION", "Your card payment is still being confirmed. Please wait a moment.");
        if (attempt is null || !attempt.OrderId.StartsWith("goride-", StringComparison.Ordinal)
            || attempt.AmountMinor != amountMinor || attempt.Currency != PayHereSettings.Currency)
        {
            attempt = new(tripId, "goride-" + Guid.NewGuid().ToString("N"), amountMinor, PayHereSettings.Currency,
                urls.Return, urls.Cancel, clock.GetUtcNow());
            await checkouts.InsertAsync(attempt, ct);
        }
        // SCRUM-105: remember the verified email for the receipt sent once the payment is verified.
        if (contact.EmailVerified)
            await receipts.SaveContactAsync(tripId, riderId, contact.Email, contact.ReceiptName, clock.GetUtcNow(), ct);
        return Form(attempt, urls.Notify, contact);
    }

    private CheckoutForm Form(CheckoutAttempt attempt, string notifyUrl, RiderContact contact)
    {
        var fields = new Dictionary<string, string>
        {
            ["merchant_id"] = settings.MerchantId,
            ["return_url"] = attempt.ReturnUrl,
            ["cancel_url"] = attempt.CancelUrl,
            ["notify_url"] = notifyUrl,
            ["order_id"] = attempt.OrderId,
            ["items"] = "GoRide completed ride",
            ["currency"] = attempt.Currency,
            ["amount"] = PayHereSignature.FormatAmount(attempt.AmountMinor),
            ["first_name"] = contact.FirstName,
            ["last_name"] = contact.LastName,
            ["email"] = contact.Email,
            ["phone"] = contact.Phone,
            ["address"] = "Colombo",
            ["city"] = "Colombo",
            ["country"] = "Sri Lanka",
            ["custom_1"] = attempt.TripId,
            ["hash"] = PayHereSignature.CheckoutHash(settings.MerchantId, attempt.OrderId, attempt.AmountMinor,
                attempt.Currency, settings.MerchantSecret)
        };
        return new(attempt.OrderId, PayHereSettings.CheckoutUrl, fields, attempt.AmountMinor / 100m, attempt.Currency);
    }
}
