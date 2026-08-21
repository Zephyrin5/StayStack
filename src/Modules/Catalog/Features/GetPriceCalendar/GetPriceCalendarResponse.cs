namespace Catalog.Features.GetPriceCalendar;

public record GetPriceCalendarResponse
{
    public List<PriceCalendarDay> Days { get; init; } = [];
}

// Property names are deliberately PascalCase and alias-matched exactly
// (case-insensitively) against the SQL in GetPriceCalendarHandler, rather
// than relying on a project-wide snake_case Dapper type map - see the note
// in the handler for why that decision was made explicitly rather than
// assumed.
public record PriceCalendarDay
{
    public DateOnly Date { get; init; }
    public decimal Price { get; init; }
    public bool IsAvailable { get; init; }
}
