using BuildingBlocks.Pagination;
using Catalog.Features.AdminCreateProperty;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.DeletePricingRule;
using Catalog.Features.DeleteProperty;
using Catalog.Features.DeleteUnit;
using Catalog.Features.GetHostProperties;
using Catalog.Features.GetMyProperties;
using Catalog.Features.GetPriceCalendar;
using Catalog.Features.GetProperties;
using Catalog.Features.GetPropertyById;
using Catalog.Features.HoldAvailability;
using Catalog.Features.ListPricingRules;
using Catalog.Features.UpdatePricingRule;
using Catalog.Features.UpdateProperty;
using Catalog.Features.UpdateUnit;
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
[JsonSerializable(typeof(GetHostPropertiesRequest))]
[JsonSerializable(typeof(GetPropertyByIdRequest))]
[JsonSerializable(typeof(GetPropertyByIdResponse))]
[JsonSerializable(typeof(UpdatePropertyRequest))]
[JsonSerializable(typeof(UpdatePropertyResponse))]
[JsonSerializable(typeof(DeletePropertyRequest))]
[JsonSerializable(typeof(DeletePropertyResponse))]
[JsonSerializable(typeof(UpdateUnitRequest))]
[JsonSerializable(typeof(UpdateUnitResponse))]
[JsonSerializable(typeof(DeleteUnitRequest))]
[JsonSerializable(typeof(DeleteUnitResponse))]
[JsonSerializable(typeof(CreatePricingRuleRequest))]
[JsonSerializable(typeof(CreatePricingRuleResponse))]
[JsonSerializable(typeof(UpdatePricingRuleRequest))]
[JsonSerializable(typeof(UpdatePricingRuleResponse))]
[JsonSerializable(typeof(DeletePricingRuleRequest))]
[JsonSerializable(typeof(DeletePricingRuleResponse))]
[JsonSerializable(typeof(ListPricingRulesRequest))]
[JsonSerializable(typeof(ListPricingRulesResponse))]
public partial class CatalogJsonSerializerContext : JsonSerializerContext;
