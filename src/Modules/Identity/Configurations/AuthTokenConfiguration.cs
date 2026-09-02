using System.ComponentModel.DataAnnotations;
﻿namespace Identity.Configurations;

public class AuthTokenConfiguration
{
    public const string SectionName = "Auth:Token";

    [Required(AllowEmptyStrings = false)]
    public string Key { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    [Range(1, 1440)]
    public double AccessTokenLifespanInMinutes { get; set; }
    [Range(1, 3650)]
    public double RefreshTokenLifespanInDays { get; set; }
}
