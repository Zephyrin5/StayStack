using Identity.Features.Auth.RefreshToken;
using Identity.Features.Auth.SignIn;
using Identity.Features.Auth.SignOut;
using Identity.Features.Auth.SignUp;
using Identity.Features.BecomeHost;
using Identity.Features.SignUp;
using System.Text.Json.Serialization;
namespace Identity.Serialization;

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
