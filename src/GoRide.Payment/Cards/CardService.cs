using GoRide.Payment.Services;

namespace GoRide.Payment.Cards;

// A rider's saved demo cards. Validation happens before anything is stored, and the
// full card number and CVC are discarded once validated.
public sealed class CardService(CardStore cards, TimeProvider clock)
{
    public Task<IReadOnlyList<SavedCard>> ListAsync(string riderId, CancellationToken ct) => cards.ListAsync(riderId, ct);

    public async Task<SavedCard> AddAsync(string riderId, string? number, int? expMonth, int? expYear, string? cvc,
        string? holderName, bool makeDefault, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var card = DemoCards.Validate(number, expMonth, expYear, cvc, holderName, now);
        return await cards.AddAsync(riderId, card, makeDefault, now, ct);
    }

    public async Task DeleteAsync(string riderId, string cardId, CancellationToken ct)
    {
        if (!await cards.DeleteAsync(riderId, RequireCardId(cardId), ct)) throw NotFound();
    }

    public async Task<SavedCard> SetDefaultAsync(string riderId, string cardId, CancellationToken ct) =>
        await cards.SetDefaultAsync(riderId, RequireCardId(cardId), ct) ?? throw NotFound();

    // Unknown, malformed and other riders' card IDs all look the same to the caller.
    internal static string RequireCardId(string? cardId) =>
        Guid.TryParseExact(cardId, "D", out var parsed) ? parsed.ToString("D") : throw NotFound();

    internal static PaymentException NotFound() => new(404, "CARD_NOT_FOUND", "That card is not saved on your account.");
}
