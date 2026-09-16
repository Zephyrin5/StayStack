using BuildingBlocks.Persistence;
using BuildingBlocks.Identity;
using BuildingBlocks.Configuration;
using BuildingBlocks.Security;
using Identity.Configurations;
using Identity.Entities;
using Identity.Features.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Persistence;
using System.Text;
namespace Identity;

public static class IdentityServicesRegistration
{
    public static IServiceCollection ConfigureIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {
        AuthTokenConfiguration authTokenSettings = new AuthTokenConfiguration();
        configuration.AppSection(AuthTokenConfiguration.SectionName).Bind(authTokenSettings);

        if (string.IsNullOrWhiteSpace(authTokenSettings.Key))
        {
            // Names the real key path. It said "JwtSettings:Key", a section that
            // does not exist anywhere in this codebase - a message that sends
            // someone to the wrong place is worse than no message.
            throw new InvalidOperationException(
                $"{AppConfiguration.RootSection}:{AuthTokenConfiguration.SectionName}:Key cannot be null or empty.");
        }

        services.AddOptions<AuthTokenConfiguration>()
            .Bind(configuration.AppSection(AuthTokenConfiguration.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthTokenConfiguration>, AuthTokenConfigurationValidator>();
        services.AddScoped<IAuthTokenProvider, AuthTokenProvider>();

        // Registered unconditionally, including under "Testing" - the test
        // host (IntegrationTestWebApplicationFactory) overrides this via
        // RemoveAll<DbContextOptions<...>>() + a fresh AddDbContext. Production
        // code has no "am I under test" awareness.
        services.AddDbContext<AppIdentityDbContext>(options =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException("Connection string for IdentityDbContext not found.");

            options.ConfigureStayStackDefaults(
                connectionString,
                "identity",
                environment is not null && environment.IsDevelopment());
        });
        services.AddAtomicParticipant<AppIdentityDbContext>(AtomicParticipants.Identity);
        services.AddSingleton<IModuleModel, IdentityModel>();
        services.AddScoped<IdentityDb>();

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4; // blocks "aaaaaaaaaaaa"

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        // Explicit rather than AddEntityFrameworkStores, which builds these with MakeGenericType
        // (docs/adr/0001). The context moves to AppDbContext with the rest of the module in 1.3.
        services.AddScoped<IUserStore<ApplicationUser>, UserStore<ApplicationUser, IdentityRole<Guid>, AppIdentityDbContext, Guid>>();
        services.AddScoped<IRoleStore<IdentityRole<Guid>>, RoleStore<IdentityRole<Guid>, AppIdentityDbContext, Guid>>();


        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.SaveToken = true;

                // No RequireHttpsMetadata override here, deliberately - it
                // only governs fetching a discovery document from Authority/
                // MetadataAddress, neither of which this app sets (tokens
                // are validated against the local SymmetricSecurityKey
                // below, no metadata endpoint is ever fetched). Leaving it
                // unset keeps ASP.NET Core's own default (true) as the
                // starting point should Authority-based validation ever get
                // added later, rather than an unconditional `false` set
                // once for local HTTP testing and silently inherited by
                // every environment, including production.

                // Without this, the JwtBearer handler silently remaps
                // well-known short claim names to their long ASP.NET
                // equivalents on the way in (e.g. "sub" becomes
                // ".../claims/nameidentifier") - AuthTokenProvider issues
                // "sub" and HttpContextCurrentUserProvider reads "sub" back,
                // so leaving this default true made every authenticated
                // request resolve UserId as null.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidIssuer = authTokenSettings.Issuer,
                    ValidAudience = authTokenSettings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authTokenSettings.Key))
                };
            });

        // A second bearer scheme for booking-management sessions, differing
        // from the one above in exactly one parameter: the audience it
        // accepts. That single difference is what keeps a booking session from
        // ever authenticating a user - it fails the default scheme's audience
        // check, so HttpContext.User stays anonymous on every ordinary
        // endpoint no matter what that endpoint's authorization says.
        //
        // Everything else is deliberately identical, including
        // ValidateLifetime: expiry is enforced here, by the handler, not by
        // handler code that might forget.
        services.AddAuthentication()
            .AddJwtBearer(AuthenticationSchemes.BookingSession, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidIssuer = authTokenSettings.Issuer,
                    ValidAudience = authTokenSettings.BookingSessionAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authTokenSettings.Key))
                };
            });

        // Named so a future rule is "add a const + an AddPolicy call here",
        // not a new role-name string typed out at whatever endpoint needs
        // it. HostOrAdministrator exists because combining Policies(Host,
        // Administrator) on one endpoint would be an AND of two policies,
        // not the "either role" check CreateUnit actually needs.
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Host, policy => policy.RequireRole(AuthorizationPolicies.Host))
            .AddPolicy(AuthorizationPolicies.Administrator, policy => policy.RequireRole(AuthorizationPolicies.Administrator))
            .AddPolicy(AuthorizationPolicies.HostOrAdministrator,
                policy => policy.RequireRole(AuthorizationPolicies.Host, AuthorizationPolicies.Administrator));

        return services;
    }
}
