using Identity.Features.AssignRole;
using Identity.Features.BecomeHost;
using Identity.Features.GetUsers;
using Identity.Features.RefreshToken;
using Identity.Features.RemoveRole;
using Identity.Features.SignIn;
using Identity.Features.SignOut;
using Identity.Features.SignUp;
using Identity.Outbox;
using BuildingBlocks.Pagination;
using System.Text.Json.Serialization;
namespace Identity.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeleteHostOutboxMessage))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(SignInResponse))]
[JsonSerializable(typeof(SignUpRequest))]
[JsonSerializable(typeof(SignUpResponse))]
[JsonSerializable(typeof(RefreshTokenRequest))]
[JsonSerializable(typeof(RefreshTokenResponse))]
[JsonSerializable(typeof(BecomeHostRequest))]
[JsonSerializable(typeof(BecomeHostResponse))]
[JsonSerializable(typeof(SignOutRequest))]
[JsonSerializable(typeof(SignOutResponse))]
[JsonSerializable(typeof(GetUsersRequest))]
[JsonSerializable(typeof(PagedResponse<UserSummary>))]
[JsonSerializable(typeof(AssignRoleRequest))]
[JsonSerializable(typeof(AssignRoleResponse))]
[JsonSerializable(typeof(RemoveRoleRequest))]
[JsonSerializable(typeof(RemoveRoleResponse))]
public partial class IdentityJsonSerializerContext : JsonSerializerContext;
