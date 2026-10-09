using System.Security.Claims;
using System.Text.Json.Serialization;
using GoRide.Payment.Cards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GoRide.Payment.Controllers;

// The rider's saved demo cards. Card numbers arrive only in the add request and are never
// returned, logged or stored in full.
[ApiController]
[Authorize]
[Route("payments/cards")]
public sealed class CardsController(CardService cards) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { cards = await cards.ListAsync(User.FindFirstValue("sub")!, ct) });
    }

    // The published test numbers and what each one does, for the app's helper list.
    [HttpGet("test-cards")]
    public IActionResult TestCards() => Ok(new { cards = DemoCards.TestCards });

    [HttpPost]
    public async Task<IActionResult> Add(AddCardRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var card = await cards.AddAsync(User.FindFirstValue("sub")!, request.Number, request.ExpMonth, request.ExpYear,
            request.Cvc, request.HolderName, request.MakeDefault ?? false, ct);
        return StatusCode(StatusCodes.Status201Created, card);
    }

    [HttpDelete("{cardId}")]
    public async Task<IActionResult> Delete(string cardId, CancellationToken ct)
    {
        await cards.DeleteAsync(User.FindFirstValue("sub")!, cardId, ct);
        return NoContent();
    }

    [HttpPost("{cardId}/default")]
    public async Task<IActionResult> MakeDefault(string cardId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MakeDefaultCardRequest? request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await cards.SetDefaultAsync(User.FindFirstValue("sub")!, cardId, ct));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddCardRequest(string? Number, int? ExpMonth, int? ExpYear, string? Cvc, string? HolderName, bool? MakeDefault);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MakeDefaultCardRequest;
