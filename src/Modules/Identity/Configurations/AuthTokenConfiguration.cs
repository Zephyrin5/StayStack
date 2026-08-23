namespace Identity.Configurations;

public class AuthTokenConfiguration
{
    public string Key { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public double AccessTokenLifespanInMinutes { get; set; }
    public double RefreshTokenLifespanInDays { get; set; }
}
