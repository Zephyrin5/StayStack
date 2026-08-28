using BuildingBlocks.Pagination;
using Promotions.Features;
using Promotions.Features.AdminCreatePromotion;
using Promotions.Features.CreatePromotion;
using Promotions.Features.DeletePromotion;
using Promotions.Features.GetHostPromotions;
using Promotions.Features.ListMyPromotions;
using Promotions.Features.UpdatePromotion;
using System.Text.Json.Serialization;
namespace Promotions.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreatePromotionRequest))]
[JsonSerializable(typeof(CreatePromotionResponse))]
[JsonSerializable(typeof(AdminCreatePromotionRequest))]
[JsonSerializable(typeof(UpdatePromotionRequest))]
[JsonSerializable(typeof(UpdatePromotionResponse))]
[JsonSerializable(typeof(DeletePromotionRequest))]
[JsonSerializable(typeof(DeletePromotionResponse))]
[JsonSerializable(typeof(PagedResponse<PromotionSummary>))]
[JsonSerializable(typeof(ListMyPromotionsRequest))]
[JsonSerializable(typeof(GetHostPromotionsRequest))]
public partial class PromotionsJsonSerializerContext : JsonSerializerContext;
