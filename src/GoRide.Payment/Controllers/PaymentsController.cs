using System.Security.Claims;
using System.Text.Json.Serialization;
using GoRide.Payment.Data;
using GoRide.Payment.Checkout;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

[ApiController]
[Authorize]
[Route("payments")]
public sealed class PaymentsController(PaymentStore store, CheckoutService checkout, ConfirmationService confirmations) : ControllerBase
{
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
        return Ok(await confirmations.AcknowledgeAsync(tripId, User.FindFirstValue("sub")!, request.ConfirmationId!, ct));
    }
}

public sealed record AcknowledgeConfirmationRequest(string? ConfirmationId);

// Reject caller-supplied fare, identity, status, and other unknown fields.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectMethodRequest(string? Method);

// The caller cannot override the amount, currency, provider, or return URL.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckoutRequest;
