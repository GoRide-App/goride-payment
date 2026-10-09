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

public sealed record PayResult(string Status, bool AlreadyPaid, PaymentConfirmation Confirmation);

// Charges a saved demo card in-app, like a Stripe card-on-file payment. The charge is
// recorded through the same verification path as a PayHere notice, so a success marks the
// trip paid together with its confirmation and email receipt, exactly once.
public sealed class CardPaymentService(PaymentStore payments, CheckoutStore checkouts, VerificationStore verifications,
    ConfirmationStore confirmations, CardStore cards, ReceiptStore receipts, IConfiguration configuration,
    TimeProvider clock, ILogger<CardPaymentService> logger)
{
    public const string Provider = "DemoCard";

    public async Task<PayResult> PayAsync(string tripId, string riderId, string? cardId, RiderContact contact, CancellationToken ct)
    {
        // Validate the request before touching the database.
        PaymentRules.ValidateId(tripId, "tripId");
        var id = CardService.RequireCardId(cardId);
        // The trip lock makes a double tap charge once: the second request sees the trip paid.
        await using var tripLock = await checkouts.LockAsync(tripId, ct);
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can pay for it.");
        if (payment.Status is "Paid" or "Charged" && await confirmations.GetAsync(tripId, ct) is { } paid)
            return new("Paid", true, paid);
        PaymentRules.SelectCard(payment, riderId); // Settled, disabled and cash payments cannot be charged.
        if (payment.FinalFare <= 0 || payment.FinalFare * 100 > 99999999)
            throw new PaymentException(409, "CHECKOUT_AMOUNT_UNSUPPORTED", "This fare cannot be paid by card.");
        var (card, behaviour) = await cards.GetAsync(riderId, id, ct) ?? throw CardService.NotFound();

        // SCRUM-105: the receipt goes to the verified email on the rider's account.
        if (contact.EmailVerified)
            await receipts.SaveContactAsync(tripId, riderId, contact.Email, contact.ReceiptName, clock.GetUtcNow(), ct);
        // A card network takes a moment; the app shows its processing state meanwhile.
        var processing = configuration.GetValue("DemoCard:ProcessingMilliseconds", 1500);
        if (processing > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(processing, 10000)), ct);

        var now = clock.GetUtcNow();
        var amountMinor = decimal.ToInt64(payment.FinalFare * 100);
        var order = new CheckoutAttempt(tripId, "demo-" + Guid.NewGuid().ToString("N"), amountMinor, PayHereSettings.Currency, "", "", now);
        await checkouts.InsertAsync(order, ct);
        if (DemoCards.IsExpired(card.ExpMonth, card.ExpYear, now)) behaviour = CardBehaviour.ExpiredCard;
        var decline = DemoCards.Decline(behaviour);
        var paymentId = "demo_" + Guid.NewGuid().ToString("N")[..24];
        var statusCode = decline is null ? 2 : -2;
        var notice = new VerificationRecord(Provider, paymentId, statusCode, order.OrderId, tripId, amountMinor,
            order.Currency, Hash(paymentId, statusCode, order.OrderId, amountMinor, card.CardId),
            card.Brand.ToUpperInvariant(), "************" + card.Last4, now);
        var result = await verifications.RecordAsync(notice, current => PaymentRules.ApplyProviderNotice(current, order, notice), ct);

        if (decline is not null)
        {
            logger.LogInformation("Demo card payment for trip {TripId} declined: {Code}.", tripId, decline.Code);
            throw decline;
        }
        if (result.Outcome != VerificationOutcome.Paid)
            throw new PaymentException(409, "PAYMENT_NOT_COMPLETED", "The payment could not be completed. Please try again.");
        var confirmation = await confirmations.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "PAYMENT_NOT_COMPLETED", "The payment could not be completed. Please try again.");
        return new("Paid", false, confirmation);
    }

    private static string Hash(string paymentId, int status, string orderId, long amountMinor, string cardId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', paymentId,
            status.ToString(CultureInfo.InvariantCulture), orderId, amountMinor.ToString(CultureInfo.InvariantCulture), cardId))))
            .ToLowerInvariant();
}
