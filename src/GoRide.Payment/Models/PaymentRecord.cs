namespace GoRide.Payment.Models;

public sealed record PaymentRecord
{
    public required string Id { get; init; }
    public required string TripId { get; init; }
    public required string RiderId { get; init; }
    public required string DriverId { get; init; }
    public decimal EstimatedFare { get; init; }
    public decimal FinalFare { get; init; }
    public FareBreakdown? Breakdown { get; init; }
    public string? Method { get; init; }
    public int CardAttemptCount { get; init; }
    public bool CardDisabled { get; init; }
    public string Status { get; init; } = "Pending";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset FareUpdatedAt { get; init; }
}

public sealed record FareBreakdown(decimal Base, decimal Distance, decimal Time,
    decimal Stops, decimal Waiting, decimal Total);
