namespace Api.RateLimiting;

public class AuthRateLimitOptions
{
    public int AuthPermitLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
}
