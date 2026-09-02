using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class PendingHostLinkIntentConfiguration : IEntityTypeConfiguration<PendingHostLinkIntent>
{
    public void Configure(EntityTypeBuilder<PendingHostLinkIntent> builder)
    {
        builder.HasKey(i => i.Id);

        // At most one live intent per user, and load-bearing rather than
        // documentary - it is what bounds the orphan count at one.
        // BecomeHostHandler reuses an existing row's Id on a retry, so this
        // index is the backstop for the case where two attempts race past that
        // lookup: the second insert fails before RegisterHostAsync is ever
        // called, rather than allocating a second Host.
        //
        // Plain, not partial - resolving an intent deletes the row, so there
        // is no resolved state left to filter out. Named explicitly per
        // ADR-0011's gotcha.
        builder.HasIndex(i => i.UserId, "ix_pending_host_link_intents_user_id")
            .IsUnique()
            .HasDatabaseName("ix_pending_host_link_intents_user_id");

        // What the reconcile job scans by.
        builder.HasIndex(i => i.CreatedAt, "ix_pending_host_link_intents_created_at")
            .HasDatabaseName("ix_pending_host_link_intents_created_at");
    }
}
