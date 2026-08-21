using Catalog.Features.AdminCreateProperty;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.GetPriceCalendar;
using Catalog.Features.HoldAvailability;
using System.Text.Json.Serialization;
namespace Catalog.Serialization;

[JsonSerializable(typeof(CreatePropertyRequest))]
[JsonSerializable(typeof(CreatePropertyResponse))]
[JsonSerializable(typeof(AdminCreatePropertyRequest))]
[JsonSerializable(typeof(CreateUnitRequest))]
[JsonSerializable(typeof(CreateUnitResponse))]
[JsonSerializable(typeof(GetPriceCalendarRequest))]
[JsonSerializable(typeof(GetPriceCalendarResponse))]
[JsonSerializable(typeof(HoldAvailabilityRequest))]
[JsonSerializable(typeof(HoldAvailabilityResponse))]
public partial class CatalogJsonSerializerContext : JsonSerializerContext;
