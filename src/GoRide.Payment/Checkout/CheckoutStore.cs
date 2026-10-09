using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Data;
using GoRide.Payment.Services;
using MySqlConnector;

namespace GoRide.Payment.Checkout;

public sealed class CheckoutStore(PaymentStore payments)
{
    // MySQL locks coordinate checkout/fare changes across processes. The intent is
    // committed before calling Stripe, so a lost response is recoverable after a crash.
    public async Task<TripLease> LockAsync(string tripId, CancellationToken ct)
    {
        var connection = payments.CreateConnection();
        var key = "pay:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tripId)))[..60];
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand("SELECT GET_LOCK(@key, 5)", connection);
            command.Parameters.AddWithValue("@key", key);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
                throw new PaymentException(409, "CHECKOUT_BUSY", "Checkout is being prepared. Retry this trip shortly.");
            return new TripLease(connection, key);
        }
        catch { MySqlConnection.ClearPool(connection); await connection.DisposeAsync(); throw; }
    }

    public async Task<CheckoutAttempt?> LatestAsync(string tripId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT idempotency_key, amount_minor, currency, success_url, cancel_url, created_at, provider_session_id
            FROM payment_checkouts WHERE trip_id = @trip ORDER BY id DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(tripId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)), reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task InsertAsync(CheckoutAttempt attempt, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            INSERT INTO payment_checkouts (trip_id, idempotency_key, amount_minor, currency, success_url, cancel_url, created_at)
            VALUES (@trip, @key, @amount, @currency, @success, @cancel, @created)
            """, connection);
        command.Parameters.AddWithValue("@trip", attempt.TripId);
        command.Parameters.AddWithValue("@key", attempt.IdempotencyKey);
        command.Parameters.AddWithValue("@amount", attempt.AmountMinor);
        command.Parameters.AddWithValue("@currency", attempt.Currency);
        command.Parameters.AddWithValue("@success", attempt.SuccessUrl);
        command.Parameters.AddWithValue("@cancel", attempt.CancelUrl);
        command.Parameters.AddWithValue("@created", attempt.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveSessionAsync(CheckoutAttempt attempt, HostedSession session, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            UPDATE payment_checkouts SET provider_session_id = @session
            WHERE idempotency_key = @key AND (provider_session_id IS NULL OR provider_session_id = @session)
            """, connection);
        command.Parameters.AddWithValue("@session", session.Id);
        command.Parameters.AddWithValue("@key", attempt.IdempotencyKey);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new PaymentException(409, "CHECKOUT_CONFLICT", "The checkout could not be recorded. Retry the same trip.");
    }
}

public sealed class TripLease(MySqlConnection connection, string key) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new MySqlCommand("SELECT RELEASE_LOCK(@key)", connection);
            command.Parameters.AddWithValue("@key", key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch { MySqlConnection.ClearPool(connection); throw; }
        finally { await connection.DisposeAsync(); }
    }
}
