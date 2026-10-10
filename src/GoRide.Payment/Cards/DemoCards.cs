using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Services;

namespace GoRide.Payment.Cards;

// What a demo card does when it is charged, like Stripe's test cards.
public static class CardBehaviour
{
    public const string Succeeds = "Succeeds";
    public const string Declined = "Declined";
    public const string InsufficientFunds = "InsufficientFunds";
    public const string ExpiredCard = "ExpiredCard";
    public const string IncorrectCvc = "IncorrectCvc";
    public const string ProcessingError = "ProcessingError";
}

public sealed record DemoTestCard(string Number, string Brand, string Behaviour, string Description);

// A card that passed validation. Only the brand, last four digits, expiry and a
// fingerprint are kept; the full number and CVC are dropped after validation.
public sealed record ValidatedCard(string Brand, string Last4, int ExpMonth, int ExpYear, string? HolderName,
    string Behaviour, string Fingerprint);

// Demo card rules, kept pure so they are easy to unit test. Only the published test
// numbers are accepted, so nobody can enter a real card into the demo.
public static class DemoCards
{
    public const int MaxCardsPerRider = 1;

    public static readonly IReadOnlyList<DemoTestCard> TestCards =
    [
        new("4242424242424242", "Visa", CardBehaviour.Succeeds, "Payment succeeds"),
        new("5555555555554444", "Mastercard", CardBehaviour.Succeeds, "Payment succeeds"),
        new("4916217501611292", "Visa", CardBehaviour.Succeeds, "PayHere sandbox card, payment succeeds"),
        new("4000000000000002", "Visa", CardBehaviour.Declined, "Card is declined"),
        new("4000000000009995", "Visa", CardBehaviour.InsufficientFunds, "Declined for insufficient funds"),
        new("4000000000000069", "Visa", CardBehaviour.ExpiredCard, "Declined as expired"),
        new("4000000000000127", "Visa", CardBehaviour.IncorrectCvc, "Declined for an incorrect CVC"),
        new("4000000000000119", "Visa", CardBehaviour.ProcessingError, "Processing error, retry succeeds with another card")
    ];

    public static ValidatedCard Validate(string? number, int? expMonth, int? expYear, string? cvc, string? holderName, DateTimeOffset now)
    {
        var digits = new string((number ?? "").Where(c => c is not (' ' or '-')).ToArray());
        if (digits.Length is < 12 or > 19 || !digits.All(char.IsAsciiDigit))
            throw Invalid("CARD_NUMBER_INVALID", "Your card number is incomplete.");
        if (!PassesLuhn(digits))
            throw Invalid("CARD_NUMBER_INVALID", "Your card number is invalid.");
        if (expMonth is not (>= 1 and <= 12) || expYear is null)
            throw Invalid("CARD_EXPIRY_INVALID", "Your card's expiration date is incomplete.");
        var year = expYear < 100 ? 2000 + expYear.Value : expYear.Value;
        var today = now.ToOffset(TimeSpan.FromMinutes(330));
        if (year < today.Year || (year == today.Year && expMonth < today.Month))
            throw Invalid("CARD_EXPIRED", "Your card's expiration date is in the past.");
        if (year > today.Year + 20)
            throw Invalid("CARD_EXPIRY_INVALID", "Your card's expiration year is invalid.");
        if (cvc is null || cvc.Length != 3 || !cvc.All(char.IsAsciiDigit))
            throw Invalid("CARD_CVC_INVALID", "Your card's security code is incomplete.");
        var name = string.IsNullOrWhiteSpace(holderName) ? null : holderName.Trim();
        if (name is { Length: > 100 } || name?.Any(char.IsControl) == true)
            throw Invalid("CARD_NAME_INVALID", "Enter the name on the card in 100 characters or fewer.");
        var test = TestCards.FirstOrDefault(card => card.Number == digits)
            ?? throw Invalid("CARD_NOT_TEST_CARD", "This is a demo, so only GoRide test cards are accepted. Try 4242 4242 4242 4242.");
        return new(test.Brand, digits[^4..], expMonth.Value, year, name, test.Behaviour,
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(digits))).ToLowerInvariant());
    }

    public static bool PassesLuhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    public static bool IsExpired(int expMonth, int expYear, DateTimeOffset now)
    {
        var today = now.ToOffset(TimeSpan.FromMinutes(330));
        return expYear < today.Year || (expYear == today.Year && expMonth < today.Month);
    }

    // The decline a charge returns, or null when the charge succeeds. 402 like Stripe.
    public static PaymentException? Decline(string behaviour) => behaviour switch
    {
        CardBehaviour.Succeeds => null,
        CardBehaviour.InsufficientFunds => new(402, "INSUFFICIENT_FUNDS", "Your card has insufficient funds. Try another card."),
        CardBehaviour.ExpiredCard => new(402, "EXPIRED_CARD", "Your card has expired. Try another card."),
        CardBehaviour.IncorrectCvc => new(402, "INCORRECT_CVC", "Your card's security code is incorrect. Check it or try another card."),
        CardBehaviour.ProcessingError => new(402, "PROCESSING_ERROR", "Your card could not be processed. Try again or use another card."),
        _ => new(402, "CARD_DECLINED", "Your card was declined. Try another card.")
    };

    private static PaymentException Invalid(string code, string message) => new(400, code, message);
}
