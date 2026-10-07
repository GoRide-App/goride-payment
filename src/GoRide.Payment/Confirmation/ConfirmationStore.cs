using GoRide.Payment.Data;
using MySqlConnector;

namespace GoRide.Payment.Confirmation;

// Parameterised ADO.NET access to payment_confirmations. Rows are only ever inserted by
// VerificationStore together with the paid state; this store reads and acknowledges them.
public sealed class ConfirmationStore(PaymentStore payments)
{
    public async Task<PaymentConfirmation?> GetAsync(string tripId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        return await ReadAsync(connection, tripId, ct);
    }

    // Sets acknowledged_at once; repeating the call keeps the first time. Returns null when
    // the trip has no confirmation with this ID.
    public async Task<PaymentConfirmation?> AcknowledgeAsync(string tripId, string confirmationId, DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using (var command = new MySqlCommand("""
            UPDATE payment_confirmations SET acknowledged_at = COALESCE(acknowledged_at, @at)
            WHERE trip_id = @trip AND confirmation_id = @confirmation
            """, connection))
        {
            command.Parameters.AddWithValue("@at", at.UtcDateTime);
            command.Parameters.AddWithValue("@trip", tripId);
            command.Parameters.AddWithValue("@confirmation", confirmationId);
            await command.ExecuteNonQueryAsync(ct);
        }
        var confirmation = await ReadAsync(connection, tripId, ct);
        return confirmation?.ConfirmationId == confirmationId ? confirmation : null;
    }

    private static async Task<PaymentConfirmation?> ReadAsync(MySqlConnection connection, string tripId, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT confirmation_id, amount_minor, currency, payment_method, card_masked, provider_payment_id, paid_at, acknowledged_at
            FROM payment_confirmations WHERE trip_id = @trip
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var card = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new(
            Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!, tripId,
            reader.GetInt64(1) / 100m, reader.GetString(2), "Card",
            reader.IsDBNull(3) ? null : reader.GetString(3),
            card is { Length: >= 4 } && card[^4..].All(char.IsAsciiDigit) ? card[^4..] : null,
            reader.GetString(5), Utc(reader.GetDateTime(6)), reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7)));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
