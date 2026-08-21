namespace Api.Localization;

public record LanguageDto
{
    public string Code { get; init; } = string.Empty;
    public string NativeName { get; init; } = string.Empty;
    public bool IsRtl { get; init; }
}
