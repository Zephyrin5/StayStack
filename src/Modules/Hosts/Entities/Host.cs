using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.ValueObjects;
namespace Hosts.Entities;

public sealed class Host : Entity
{

    // See Property.cs for why materialization goes through a real
    // constructor rather than a parameterless one + reflection-set
    // properties.
    private Host(Guid id, string businessName, string contactEmail, string? contactPhone, LocalizedText? displayName)
    {
        Id = id;
        BusinessName = businessName;
        ContactEmail = contactEmail;
        ContactPhone = contactPhone;
        DisplayName = displayName;
    }

    // Legal/registration identity - matches the business registration,
    // the myFatoorah merchant record, invoices. Deliberately a single
    // plain string, not LocalizedText: this isn't content that should
    // ever have independently-editable per-language "versions" that could
    // drift from what's actually on file.
    public string BusinessName { get; private set; }

    public string ContactEmail { get; private set; }
    public string? ContactPhone { get; private set; }

    // Optional customer-facing presentation, distinct from BusinessName -
    // an owner may want their business shown differently per language
    // without that touching their legal name on file. Null means "no
    // customization yet" - callers fall back to BusinessName.
    public LocalizedText? DisplayName { get; private set; }

    public static Host Create(
        string businessName,
        string contactEmail,
        string? contactPhone,
        LocalizedText? displayName = null)
    {
        Guard.Against.NullOrWhiteSpace(businessName);
        Guard.Against.NullOrWhiteSpace(contactEmail);
        Guard.Against.InvalidFormat(contactEmail, nameof(contactEmail),
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$", "Contact email is not a valid email address.");

        return new Host(Guid.CreateVersion7(), businessName, contactEmail, contactPhone, displayName);
    }

    public void UpdateContactInfo(string contactEmail, string? contactPhone)
    {
        Guard.Against.NullOrWhiteSpace(contactEmail);
        Guard.Against.InvalidFormat(contactEmail, nameof(contactEmail),
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$", "Contact email is not a valid email address.");

        ContactEmail = contactEmail;
        ContactPhone = contactPhone;
    }

    // null clears the customization, reverting presentation to BusinessName -
    // a deliberate, valid state, not an error, so no guard clause here.
    public void SetDisplayName(LocalizedText? displayName)
    {
        DisplayName = displayName;
    }
}
