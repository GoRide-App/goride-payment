using System.Data;
using GoRide.Payment.Data;
using GoRide.Payment.Services;
using MySqlConnector;

namespace GoRide.Payment.Cards;

// A saved demo card as the rider app sees it. Never contains the full number or CVC.
public sealed record SavedCard(string CardId, string Brand, string Last4, int ExpMonth, int ExpYear,
    string? HolderName, bool IsDefault, DateTimeOffset CreatedAt);

// Parameterised ADO.NET access to payment_cards. Every query is scoped to the rider.
public sealed class CardStore(PaymentStore payments)
{
    private const string Columns = "card_id, brand, last4, exp_month, exp_year, holder_name, is_default, created_at, test_behaviour";

    public async Task<IReadOnlyList<SavedCard>> ListAsync(string riderId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(
            $"SELECT {Columns} FROM payment_cards WHERE rider_id = @rider ORDER BY is_default DESC, created_at DESC", connection);
        command.Parameters.AddWithValue("@rider", riderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var cards = new List<SavedCard>();
        while (await reader.ReadAsync(ct)) cards.Add(Read(reader).Card);
        return cards;
    }

    public async Task<(SavedCard Card, string Behaviour)?> GetAsync(string riderId, string cardId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(
            $"SELECT {Columns} FROM payment_cards WHERE rider_id = @rider AND card_id = @card", connection);
        command.Parameters.AddWithValue("@rider", riderId);
        command.Parameters.AddWithValue("@card", cardId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    // The rider's existing rows are locked so the limit and the single default hold.
    public async Task<SavedCard> AddAsync(string riderId, ValidatedCard card, bool makeDefault, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var count = (await LockRiderCardsAsync(connection, transaction, riderId, ct)).Count;
        if (count >= DemoCards.MaxCardsPerRider)
            throw new PaymentException(409, "CARD_LIMIT_REACHED", $"You can save up to {DemoCards.MaxCardsPerRider} cards. Remove one to add another.");
        var isDefault = makeDefault || count == 0;
        if (isDefault) await ClearDefaultAsync(connection, transaction, riderId, ct);
        var saved = new SavedCard(Guid.NewGuid().ToString(), card.Brand, card.Last4, card.ExpMonth, card.ExpYear,
            card.HolderName, isDefault, now);
        await using (var insert = new MySqlCommand("""
            INSERT INTO payment_cards (card_id, rider_id, brand, last4, exp_month, exp_year, holder_name,
                test_behaviour, fingerprint, is_default, created_at)
            VALUES (@card, @rider, @brand, @last4, @month, @year, @name, @behaviour, @fingerprint, @default, @created)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@card", saved.CardId);
            insert.Parameters.AddWithValue("@rider", riderId);
            insert.Parameters.AddWithValue("@brand", card.Brand);
            insert.Parameters.AddWithValue("@last4", card.Last4);
            insert.Parameters.AddWithValue("@month", card.ExpMonth);
            insert.Parameters.AddWithValue("@year", card.ExpYear);
            insert.Parameters.AddWithValue("@name", (object?)card.HolderName ?? DBNull.Value);
            insert.Parameters.AddWithValue("@behaviour", card.Behaviour);
            insert.Parameters.AddWithValue("@fingerprint", card.Fingerprint);
            insert.Parameters.AddWithValue("@default", isDefault);
            insert.Parameters.AddWithValue("@created", now.UtcDateTime);
            try { await insert.ExecuteNonQueryAsync(ct); }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                throw new PaymentException(409, "CARD_ALREADY_SAVED", "This card is already saved.");
            }
        }
        await transaction.CommitAsync(ct);
        return saved;
    }

    // Removing the default card makes the newest remaining card the default.
    public async Task<bool> DeleteAsync(string riderId, string cardId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var cards = await LockRiderCardsAsync(connection, transaction, riderId, ct);
        if (!cards.Any(c => c.CardId == cardId)) return false;
        await using (var delete = new MySqlCommand(
            "DELETE FROM payment_cards WHERE rider_id = @rider AND card_id = @card", connection, transaction))
        {
            delete.Parameters.AddWithValue("@rider", riderId);
            delete.Parameters.AddWithValue("@card", cardId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        if (cards.Single(c => c.CardId == cardId).IsDefault
            && cards.Where(c => c.CardId != cardId).MaxBy(c => c.CreatedAt) is { } next)
            await MarkDefaultAsync(connection, transaction, riderId, next.CardId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<SavedCard?> SetDefaultAsync(string riderId, string cardId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var cards = await LockRiderCardsAsync(connection, transaction, riderId, ct);
        if (!cards.Any(c => c.CardId == cardId)) return null;
        await ClearDefaultAsync(connection, transaction, riderId, ct);
        await MarkDefaultAsync(connection, transaction, riderId, cardId, ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(riderId, cardId, ct))?.Card;
    }

    private static async Task<List<(string CardId, bool IsDefault, DateTime CreatedAt)>> LockRiderCardsAsync(
        MySqlConnection connection, MySqlTransaction transaction, string riderId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "SELECT card_id, is_default, created_at FROM payment_cards WHERE rider_id = @rider FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("@rider", riderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var cards = new List<(string, bool, DateTime)>();
        while (await reader.ReadAsync(ct))
            cards.Add((Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!,
                reader.GetBoolean(1), reader.GetDateTime(2)));
        return cards;
    }

    private static async Task ClearDefaultAsync(MySqlConnection connection, MySqlTransaction transaction, string riderId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "UPDATE payment_cards SET is_default = FALSE WHERE rider_id = @rider AND is_default", connection, transaction);
        command.Parameters.AddWithValue("@rider", riderId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkDefaultAsync(MySqlConnection connection, MySqlTransaction transaction,
        string riderId, string cardId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "UPDATE payment_cards SET is_default = TRUE WHERE rider_id = @rider AND card_id = @card", connection, transaction);
        command.Parameters.AddWithValue("@rider", riderId);
        command.Parameters.AddWithValue("@card", cardId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static (SavedCard Card, string Behaviour) Read(MySqlDataReader reader) =>
        (new SavedCard(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!,
            reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc))), reader.GetString(8));
}
