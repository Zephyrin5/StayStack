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
        // every LocalizedText-typed property model-wide, same as
        // Property.Name/Unit.Name. Don't hand-roll a custom converter here
        // serializing v.Values directly - an IReadOnlyDictionary<string,string>
        // isn't the Dictionary<string,string> a JsonTypeInfo<Dictionary<...>>
        // expects, and throws InvalidCastException the moment DisplayName is
        // ever actually set.
    }
}
