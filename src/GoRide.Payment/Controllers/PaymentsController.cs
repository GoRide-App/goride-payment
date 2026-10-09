using System.Security.Claims;
using System.Text.Json.Serialization;
using GoRide.Payment.Data;
using GoRide.Payment.Checkout;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

[ApiController]
[Authorize]
[Route("payments")]
public sealed class PaymentsController(PaymentStore store, CheckoutService checkout) : ControllerBase
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
        return Ok(await checkout.CreateAsync(tripId, User.FindFirstValue("sub")!, ct));
    }
}

// Reject caller-supplied fare, identity, status, and other unknown fields.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectMethodRequest(string? Method);

// The caller cannot override the amount, currency, provider, or return URL.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckoutRequest;
