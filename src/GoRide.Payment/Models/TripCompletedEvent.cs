namespace GoRide.Payment.Models;

// Matches the trip service envelope on goride.trip.events. Only a trusted
// completion event may establish the payable fare; rider requests never supply it.
public sealed record TripCompletedEvent
{
    public string? EventId { get; init; }
    public string? EventType { get; init; }
    public string? TripId { get; init; }
    public string? RiderId { get; init; }
    public string? DriverId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public CompletedFare? Payload { get; init; }
}

public sealed record CompletedFare
{
    public decimal? FinalFare { get; init; }
    public decimal? Fare { get; init; }
    public decimal? EstimatedFare { get; init; }
    public FareBreakdown? Breakdown { get; init; }
}
