using System.Security.Claims;

namespace GoRide.Payment.Checkout;

// OrderId is the PayHere order_id. It is stored in payment_checkouts.idempotency_key,
// so one order maps to exactly one trip and one fixed amount.
public sealed record CheckoutAttempt(
    string TripId, string OrderId, long AmountMinor, string Currency,
    string ReturnUrl, string CancelUrl, DateTimeOffset CreatedAt);

// The browser posts these fields to PayHere. The hash is safe to expose; the merchant
// secret that produced it never leaves the server.
public sealed record CheckoutForm(string OrderId, string ActionUrl, IReadOnlyDictionary<string, string> Fields,
    decimal Amount, string Currency);

// PayHere requires customer contact fields. Only identity-verified claims are used;
// sandbox placeholders fill anything the identity service does not provide.
// EmailVerified is true only when Email came from the identity session; only then is it
// stored for the SCRUM-105 email receipt (placeholders are never emailed).
public sealed record RiderContact(string FirstName, string LastName, string Email, string Phone,
    bool EmailVerified = false, string? ReceiptName = null)
{
    public static RiderContact From(ClaimsPrincipal user)
    {
        var name = (user.FindFirstValue("name") ?? "").Trim();
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var email = user.FindFirstValue("email");
        return new(
            Clean(parts.ElementAtOrDefault(0), "GoRide"),
            Clean(parts.ElementAtOrDefault(1), "Rider"),
            Clean(email, "rider@goride.lk"),
            Clean(user.FindFirstValue("phone_number"), "0770000000"),
            Receipts.ReceiptRules.IsDeliverableEmail(email),
            // The receipt greets the rider by name only when identity provided one.
            name.Length is > 0 and <= 100 && !name.Any(char.IsControl) ? name : null);
    }

    public static RiderContact Sandbox { get; } = new("GoRide", "Rider", "rider@goride.lk", "0770000000");

    private static string Clean(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(char.IsControl) ? fallback : value.Trim();
}
