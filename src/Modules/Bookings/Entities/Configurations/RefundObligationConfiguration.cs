using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class RefundObligationConfiguration : IEntityTypeConfiguration<RefundObligation>
{
    public void Configure(EntityTypeBuilder<RefundObligation> builder)
    {
        builder.ToTable("refund_obligations", BookingsModel.Schema);

        // The booking id itself, so "one obligation per booking" is enforced by
        // the key rather than by a check somebody has to remember. A second
        // writer collides; there is no interleaving that produces two.
        builder.HasKey(o => o.BookingId);

        builder.Property(o => o.PolicyRefundAmount).HasColumnType("numeric(12,3)").IsRequired();

        // Text, like every other enum in this schema - legible in psql, and
        // safe against the underlying values being reordered.
        builder.Property(o => o.Currency).HasConversion<string>().HasMaxLength(3).IsRequired();
        builder.Property(o => o.Cause).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The backstop job's entire query: unresolved obligations that are due,
        // soonest first. On NextAttemptAt rather than CancelledAt, because an
        // ordering by cancellation time lets a backlog of unpaid rows that will
        // never resolve hold the front of the queue forever. Partial, because a
        // resolved row is never scanned again and this index exists only to
        // keep that sweep off a table that grows with every cancellation the
        // platform ever takes. Named explicitly per ADR-0011's gotcha.
        builder.HasIndex(o => o.NextAttemptAt, "ix_refund_obligations_unresolved")
            .HasDatabaseName("ix_refund_obligations_unresolved")
            .HasFilter("resolved_at IS NULL");
    }
}
