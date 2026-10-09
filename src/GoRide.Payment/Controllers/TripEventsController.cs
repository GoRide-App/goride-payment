using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

[ApiController]
[Route("internal/trip-events")]
public sealed class TripEventsController(TripCompletionService store, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Complete(TripCompletedEvent evt, CancellationToken ct)
    {
        var expected = configuration["InternalServices:ApiKey"];
        var presented = Request.Headers["X-Internal-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(presented)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented)))
            throw new PaymentException(401, "INVALID_SERVICE_KEY", "A valid internal service key is required.");
        return Ok(await store.CompleteAsync(evt, ct));
    }
}
