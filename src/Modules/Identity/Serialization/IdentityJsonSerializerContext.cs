using Identity.Features.BecomeHost;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Identity.Features.SignOut;
using Identity.Features.SignUp;
using System.Text.Json.Serialization;
namespace Identity.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
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
public partial class IdentityJsonSerializerContext : JsonSerializerContext;
