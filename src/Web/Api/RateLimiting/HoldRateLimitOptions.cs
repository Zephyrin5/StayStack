namespace Api.RateLimiting;

public class HoldRateLimitOptions
{
    public int HoldPermitLimit { get; set; } = 20;
    public int HoldWindowSeconds { get; set; } = 60;
}
