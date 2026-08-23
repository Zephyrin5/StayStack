namespace Identity.Entities;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public Guid UserId { get; set; }

    // Shared by every token descended from the same original sign-in - lets
    // reuse detection revoke just this lineage (a stolen/replayed token)
    // instead of every session the user has anywhere (see
    // AuthTokenProvider.RevokeFamilyAsync). A fresh sign-in starts a new
    // family; rotation carries the same one forward.
    public Guid FamilyId { get; set; }

    // Null for a token issued at sign-in; the token this one was rotated
    // from otherwise.
    public Guid? ParentTokenId { get; set; }

    // Set when this token is consumed via rotation (not on sign-out
    // revocation) - the forward link ParentTokenId's absence can't give
    // you, useful for tracing a family's chain after the fact.
    public Guid? ReplacedByTokenId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
