using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Data;
using GoRide.Payment.Services;
using MySqlConnector;

namespace GoRide.Payment.Checkout;

public sealed class CheckoutStore(PaymentStore payments)
{
    // MySQL locks coordinate checkout, fare changes and provider verification across processes.
    public Task<TripLease> LockAsync(string tripId, CancellationToken ct) => LockKeyAsync("pay:", tripId, ct);

    // A rider may have no card rows yet. Locking existing rows alone cannot serialize
    // two simultaneous first-card saves under READ COMMITTED.
    public Task<TripLease> LockCardsAsync(string riderId, CancellationToken ct) => LockKeyAsync("card:", riderId, ct);

    private async Task<TripLease> LockKeyAsync(string prefix, string id, CancellationToken ct)
    {
        var connection = payments.CreateConnection();
        var key = prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..(64 - prefix.Length)];
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
        // idempotency_key holds the PayHere order_id; success_url holds the return URL.
        await using var command = new MySqlCommand("""
            SELECT idempotency_key, amount_minor, currency, success_url, cancel_url, created_at
            FROM payment_checkouts WHERE trip_id = @trip ORDER BY id DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(tripId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
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
        command.Parameters.AddWithValue("@key", attempt.OrderId);
        command.Parameters.AddWithValue("@amount", attempt.AmountMinor);
        command.Parameters.AddWithValue("@currency", attempt.Currency);
        command.Parameters.AddWithValue("@success", attempt.ReturnUrl);
        command.Parameters.AddWithValue("@cancel", attempt.CancelUrl);
        command.Parameters.AddWithValue("@created", attempt.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
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
