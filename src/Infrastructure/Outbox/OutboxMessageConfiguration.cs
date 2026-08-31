using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Outbox;

/// <summary>
///     Every module that owns outbox messages applies this same mapping via
///     modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration()) -
///     one shared shape, one table per module's own schema (EF resolves the
///     schema from whichever DbContext applies it, same as every other
///     per-module entity).
/// </summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        // No explicit ToTable - the DbSet<OutboxMessage> OutboxMessages
        // property name each module's DbContext exposes already resolves to
        // "outbox_messages" once UseSnakeCaseNamingConvention runs (see
        // ConfigureStayStackDefaults), same as every other entity here that
        // doesn't call ToTable by hand.
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);

        // OutboxDispatcherBase.DispatchPendingAsync's own candidate query -
        // covers the WHERE and the ORDER BY (CreatedAt, added separately
        // below) in one index.
        builder.HasIndex(m => new { m.ProcessedAt, m.DeadLetteredAt, m.NextAttemptAt });
        builder.HasIndex(m => m.CreatedAt);
    }
}
