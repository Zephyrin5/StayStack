using Catalog.Features.GetProperties;
using Mediator;
namespace Catalog.Features.GetMyProperties;

// Deliberately parameterless - unlike the old GetPropertiesRequest{HostId},
// there is no field for a caller to populate here at all. GetMyPropertiesHandler
// resolves the host id itself via IHostAuthorization, the same pattern
// CreatePropertyHandler already uses, so "whose properties" can only ever
// come from the caller's own token, never from client input. Reuses
// GetPropertiesResponse - same PropertySummary shape, no trust-boundary
// concern on a response type.
public record GetMyPropertiesRequest : IRequest<GetPropertiesResponse>;
