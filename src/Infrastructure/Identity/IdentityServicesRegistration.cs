using BuildingBlocks.Identity;
using Identity.Configurations;
using Identity.Entities;
using Identity.Features.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // 1. Bind and validate JwtSettings up front
        AuthTokenConfiguration authTokenSettings = new AuthTokenConfiguration();
        configuration.GetSection("Auth:Token").Bind(authTokenSettings);

        if (string.IsNullOrWhiteSpace(authTokenSettings.Key))
        {
            throw new InvalidOperationException("JwtSettings:Key cannot be null or empty.");
        }

        services.Configure<AuthTokenConfiguration>(configuration.GetSection("Auth:Token"));
        services.AddScoped<IAuthTokenProvider, AuthTokenProvider>();

        // 2. EF Core Database Context Setup
        // Registered unconditionally, including under "Testing" - the test
        // host (IntegrationTestWebApplicationFactory) is responsible for
        // overriding this via services.RemoveAll<DbContextOptions<...>>()
        // + a fresh AddDbContext call, not for this method knowing it's
        // being run under tests at all. Production registration code
        // having no awareness of "am I under test" is the point: every
        // module used to independently guard this with its own
        // environment check, which is exactly what made Hosts' guard
        // wrong in a way nothing caught for a while.
        services.AddDbContext<AppIdentityDbContext>(options =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException("Connection string for IdentityDbContext not found.");

            options.ConfigureStayStackDefaults(
                connectionString,
                "identity",
                environment is not null && environment.IsDevelopment());
        });

        // 3. ASP.NET Core Identity Setup
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
            .AddEntityFrameworkStores<AppIdentityDbContext>()
            .AddDefaultTokenProviders()
            .AddSignInManager();


        // 4. Authentication & JWT Bearer Setup
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
                options.RequireHttpsMetadata = false;

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
