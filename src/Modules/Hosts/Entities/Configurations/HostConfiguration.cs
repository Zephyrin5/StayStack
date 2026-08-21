using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Hosts.Entities.Configurations;

public class HostConfiguration : IEntityTypeConfiguration<Host>
{
    public void Configure(EntityTypeBuilder<Host> builder)
    {
        builder.HasKey(o => o.Id);

        builder.Property(o => o.BusinessName).HasMaxLength(200).IsRequired();
        builder.Property(o => o.ContactEmail).HasMaxLength(200).IsRequired();
        builder.Property(o => o.ContactPhone).HasMaxLength(50);

        // DisplayName needs no HasConversion of its own - StayStackDbContext's
        // ConfigureConventions already applies LocalizedTextConverter to
        // every LocalizedText-typed property model-wide (nullable reference
        // annotations don't change the CLR type, so this nullable property
        // matches that convention the same as Property.Name/Unit.Name do).
        // This used to hand-roll its own converter here, serializing
        // v.Values (an IReadOnlyDictionary<string,string>) through a
        // JsonTypeInfo<Dictionary<string,string>> - a runtime type it isn't,
        // which threw InvalidCastException the moment DisplayName was ever
        // actually set to a non-null value (nothing had been, until
        // CreateHostEndpoint). LocalizedTextConverter doesn't have this bug.
    }
}
