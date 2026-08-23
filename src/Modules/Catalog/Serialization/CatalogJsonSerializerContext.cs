using BuildingBlocks.Pagination;
using Catalog.Features.AdminCreateProperty;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.GetMyProperties;
using Catalog.Features.GetPriceCalendar;
using Catalog.Features.GetProperties;
using Catalog.Features.GetPropertyById;
using Catalog.Features.HoldAvailability;
using System.Text.Json.Serialization;
namespace Catalog.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreatePropertyRequest))]
[JsonSerializable(typeof(CreatePropertyResponse))]
[JsonSerializable(typeof(AdminCreatePropertyRequest))]
[JsonSerializable(typeof(CreateUnitRequest))]
[JsonSerializable(typeof(CreateUnitResponse))]
[JsonSerializable(typeof(GetPriceCalendarRequest))]
[JsonSerializable(typeof(GetPriceCalendarResponse))]
[JsonSerializable(typeof(HoldAvailabilityRequest))]
[JsonSerializable(typeof(HoldAvailabilityResponse))]
[JsonSerializable(typeof(GetPropertiesRequest))]
[JsonSerializable(typeof(PagedResponse<PropertySummary>))]
[JsonSerializable(typeof(GetMyPropertiesRequest))]
[JsonSerializable(typeof(GetPropertyByIdRequest))]
[JsonSerializable(typeof(GetPropertyByIdResponse))]
public partial class CatalogJsonSerializerContext : JsonSerializerContext;
