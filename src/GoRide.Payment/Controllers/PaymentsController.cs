using System.Security.Claims;
using System.Text.Json.Serialization;
using GoRide.Payment.Cards;
using GoRide.Payment.Data;
using GoRide.Payment.Checkout;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Receipts;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GoRide.Payment.Controllers;

[ApiController]
[Authorize]
[Route("payments")]
public sealed class PaymentsController(PaymentStore store, CheckoutService checkout, ConfirmationService confirmations,
    ReceiptService receipts, CardPaymentService cardPayments, PaymentStatusService statuses,
    IConfiguration configuration) : ControllerBase
{
    // Shared by the trip's rider and driver; JSON null until the completed trip arrives.
    [HttpGet("{tripId}/status")]
    public async Task<IActionResult> Status(string tripId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(await statuses.GetAsync(tripId, User.FindFirstValue("sub")!, ct)) { StatusCode = 200 };
    }

    // Charges a saved demo card in-app. Declines return 402 with a code the app explains.
    [HttpPost("{tripId}/pay")]
    public async Task<IActionResult> Pay(string tripId, PayRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await cardPayments.PayAsync(tripId, User.FindFirstValue("sub")!, request.CardId, request.RequestId, RiderContact.From(User), ct));
    }

    // Opt-in demo (DemoTrips:Enabled): records a simulated ride the
    // trip service never saw, so it can be paid and receipted like a real one.
    [HttpPost("demo-completions")]
    public async Task<IActionResult> CompleteDemoTrip(DemoCompletionRequest request, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("DemoTrips:Enabled")) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(await statuses.CompleteDemoTripAsync(request.TripId, request.FinalFare, User.FindFirstValue("sub")!, ct));
    }

    [HttpGet("{tripId}")]
    public async Task<IActionResult> Get(string tripId, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        var payment = await store.GetAsync(tripId, ct);
        if (payment is null) return new JsonResult(null) { StatusCode = 200 };
        if (payment.RiderId != User.FindFirstValue("sub"))
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can view its payment.");
        return Ok(payment);
    }

    [HttpPost("{tripId}/select-method")]
    public async Task<IActionResult> SelectMethod(string tripId, SelectMethodRequest request, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        if (request.Method != "Card")
            throw new PaymentException(400, "INVALID_PAYMENT_METHOD", "method must be Card.");
        return Ok(await store.SelectCardAsync(tripId, User.FindFirstValue("sub")!, ct));
    }

    [HttpPost("{tripId}/checkout")]
    public async Task<IActionResult> Checkout(string tripId, CheckoutRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await checkout.CreateAsync(tripId, User.FindFirstValue("sub")!, RiderContact.From(User), ct));
    }

    // SCRUM-104: polled by the app after returning from PayHere.
    [HttpGet("{tripId}/confirmation")]
    public async Task<IActionResult> Confirmation(string tripId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await confirmations.GetAsync(tripId, User.FindFirstValue("sub")!, ct));
    }

    // Records that the app has shown the confirmation, so it is shown once.
    [HttpPost("{tripId}/confirmation/acknowledge")]
    public async Task<IActionResult> AcknowledgeConfirmation(string tripId, AcknowledgeConfirmationRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await confirmations.AcknowledgeAsync(tripId, User.FindFirstValue("sub")!, request.ConfirmationId, ct));
    }

    // SCRUM-105: whether the email receipt was sent, without exposing the full address.
    [HttpGet("{tripId}/receipt")]
    public async Task<IActionResult> Receipt(string tripId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await receipts.GetAsync(tripId, User.FindFirstValue("sub")!, ct));
    }

    [HttpPost("{tripId}/receipt/resend")]
    public async Task<IActionResult> ResendReceipt(string tripId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ResendReceiptRequest? request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Accepted(await receipts.ResendAsync(tripId, User.FindFirstValue("sub")!, ct));
    }
}

// Only the saved card is chosen; amount, currency and identity come from the server.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PayRequest(string? CardId, string? RequestId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DemoCompletionRequest(string? TripId, decimal? FinalFare);

// Resend takes no fields: the receipt always goes to the address captured at checkout.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResendReceiptRequest;

// Only the confirmation ID is accepted; the caller cannot set amounts, times or identity.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcknowledgeConfirmationRequest(string? ConfirmationId);

// Reject caller-supplied fare, identity, status, and other unknown fields.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectMethodRequest(string? Method);

// The caller cannot override the amount, currency, provider, or return URL.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckoutRequest;
