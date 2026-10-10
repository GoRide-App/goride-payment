using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Checkout;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Data;
using GoRide.Payment.Receipts;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;

namespace GoRide.Payment.Cards;

public sealed record PayResult(string Status, bool AlreadyPaid, PaymentConfirmation Confirmation, int Attempts, bool AutoRetried);

// The trip lease covers both attempts and fare updates across service instances. Durable
// requests replay failures too; deterministic demo outcomes make crash recovery idempotent.
public sealed class CardPaymentService(PaymentStore payments, CheckoutStore checkouts, VerificationStore verifications,
    ConfirmationStore confirmations, CardStore cards, CardAttemptStore attempts, ReceiptStore receipts,
    IConfiguration configuration, TimeProvider clock, ILogger<CardPaymentService> logger)
{
    public const string Provider = "DemoCard";

    public async Task<PayResult> PayAsync(string tripId, string riderId, string? cardId, string? requestId,
        RiderContact contact, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        var key = CardRetryRules.RequireRequestId(requestId);
        await using var tripLock = await checkouts.LockAsync(tripId, ct);
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can pay for it.");
        if (payment.Status is "Paid" or "Charged") return await PaidAsync(tripId, true, ct);
        var id = CardService.RequireCardId(cardId);
        var request = await attempts.GetAsync(tripId, key, ct);
        if (request is not null && request.CardId != id)
            throw new PaymentException(409, "PAYMENT_REQUEST_CONFLICT", "This requestId was already used with another card. Start a new payment request.");
        if (request?.State == "Failed") throw CardRetryRules.Failure(request.LastFailureCode!, request.Attempts);
        PaymentRules.SelectCard(payment, riderId);
        if (payment.FinalFare <= 0 || payment.FinalFare * 100 > 99999999)
            throw new PaymentException(409, "CHECKOUT_AMOUNT_UNSUPPORTED", "This fare cannot be paid by card.");
        if (request is null)
        {
            var previous = await attempts.GetAsync(tripId, null, ct);
            if (previous?.State is "Processing" or "Retrying")
                throw new PaymentException(409, "PAYMENT_IN_PROGRESS", "A card payment is unfinished. Resume its request before starting another.");
            var (card, behaviour) = await cards.GetAsync(riderId, id, ct) ?? throw CardService.NotFound();
            var now = clock.GetUtcNow();
            if (DemoCards.IsExpired(card.ExpMonth, card.ExpYear, now)) behaviour = CardBehaviour.ExpiredCard;
            request = new(tripId, key, id, card.Brand, card.Last4, behaviour, "demo-" + Guid.NewGuid().ToString("N"),
                decimal.ToInt64(payment.FinalFare * 100), now);
            if (contact.EmailVerified)
                await receipts.SaveContactAsync(tripId, riderId, contact.Email, contact.ReceiptName, now, ct);
            await attempts.CreateAsync(request, ct);
        }
        if (request.AmountMinor != decimal.ToInt64(payment.FinalFare * 100))
            throw new PaymentException(409, "PAYMENT_AMOUNT_CHANGED", "The final fare changed. Refresh the payment before retrying.");

        // Once accepted, finish the bounded demo charge even if the client disconnects.
        // A process crash can still resume the persisted attempt with the same identity.
        ct = CancellationToken.None;
        while (request.State is "Processing" or "Retrying")
        {
            if (request.State == "Retrying")
                await DelayAsync("DemoCard:RetryMilliseconds", 1000, 2000, ct);
            var attempt = await attempts.StartAsync(request, clock.GetUtcNow(), ct);
            await DelayAsync("DemoCard:ProcessingMilliseconds", 1500, 10000, ct);
            var now = clock.GetUtcNow();
            var decline = DemoCards.Decline(request.Behaviour, attempt.RequestAttempt);
            var order = new CheckoutAttempt(tripId, request.OrderId, request.AmountMinor, PayHereSettings.Currency, "", "", request.CreatedAt);
            var paymentId = "demo_" + Hash(request.OrderId, attempt.RequestAttempt.ToString(CultureInfo.InvariantCulture))[..24];
            var statusCode = decline is null ? 2 : -2;
            var notice = new VerificationRecord(Provider, paymentId, statusCode, order.OrderId, tripId, request.AmountMinor,
                order.Currency, Hash(paymentId, statusCode.ToString(CultureInfo.InvariantCulture), order.OrderId,
                    request.AmountMinor.ToString(CultureInfo.InvariantCulture), request.CardId),
                request.Brand.ToUpperInvariant(), "************" + request.Last4, now);
            var updated = request with
            {
                Attempts = attempt.RequestAttempt,
                State = decline is null ? "Paid" : CardRetryRules.ShouldRetry(decline.Code, attempt.RequestAttempt) ? "Retrying" : "Failed",
                LastFailureCode = decline?.Code ?? request.LastFailureCode
            };
            await verifications.RecordAsync(notice, current => PaymentRules.ApplyProviderNotice(current, order, notice), ct,
                async (connection, transaction, decision) =>
                {
                    if (decision.Outcome != (decline is null ? VerificationOutcome.Paid : VerificationOutcome.Failed))
                        throw new PaymentException(409, "PAYMENT_NOT_COMPLETED", "The payment needs reconciliation before retrying.");
                    await CardAttemptStore.CompleteAsync(connection, transaction, updated, attempt, decline?.Code, now, ct);
                });
            request = updated;
            if (decline is not null)
                logger.LogInformation("Demo card payment for trip {TripId}, attempt {Attempt} declined: {Code}.", tripId, attempt.Number, decline.Code);
        }
        if (request.State == "Failed") throw CardRetryRules.Failure(request.LastFailureCode!, request.Attempts);
        return await PaidAsync(tripId, false, ct);
    }

    private async Task<PayResult> PaidAsync(string tripId, bool alreadyPaid, CancellationToken ct)
    {
        var confirmation = await confirmations.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "PAYMENT_NOT_COMPLETED", "The payment needs reconciliation before retrying.");
        var history = await attempts.GetAsync(tripId, null, ct);
        return new("Paid", alreadyPaid, confirmation, history?.Attempts ?? 0, history?.Attempts > 1);
    }

    private Task DelayAsync(string setting, int fallback, int maximum, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue(setting, fallback), 0, maximum)), ct);

    private static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant();
}
