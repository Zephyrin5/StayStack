using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    ///     Excludes archived rows from every query for this entity. Called by each entity's
    ///     configuration rather than applied to every <see cref="Entity"/> subtype by convention: the
    ///     convention built its predicate as an <c>Expression</c> over a CLR type discovered at
    ///     runtime, which is reflection the trimmer cannot follow (docs/aot-migration.md).
    ///     <para>
    ///         <b>This predicate is restated by hand in raw SQL</b>, as <see cref="SoftDelete"/>: a
    ///         query filter is an EF construct, so Dapper never sees it, and GetPriceCalendarHandler
    ///         once priced an archived unit's calendar because of that. The two agree only because
    ///         both are a single status comparison, which SoftDeleteFilterTests pins - along with the
    ///         fact that every entity has one, which is what the convention used to guarantee.
    ///     </para>
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasSoftDeleteFilter<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : Entity =>
        builder.HasQueryFilter(entity => entity.Status != EntityStatus.Archived);

    /// <summary>
    ///     Maps a Money-typed complex property onto the same two plain
    ///     columns every money field used before Money existed - EF Core
    ///     10's native ComplexProperty (not OwnsOne/JSONB, unlike
    ///     LocalizedText/CancellationPolicy) pinned to explicit column
    ///     names, so introducing Money is a type-only change. numeric(12,3)
    ///     everywhere - scale 3 covers every supported currency (KWD needs
    ///     it) without truncation, and one shared width keeps every money
    ///     column consistent. See docs/adr/0015.
    /// </summary>
    public static void ConfigureMoney(
        this ComplexPropertyBuilder<Money> builder,
        string amountColumnName,
        string currencyColumnName = "currency")
    {
        builder.Property(m => m.Amount).HasColumnName(amountColumnName).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(m => m.Currency).HasColumnName(currencyColumnName).HasConversion<string>().HasMaxLength(3).IsRequired();
    }
}
