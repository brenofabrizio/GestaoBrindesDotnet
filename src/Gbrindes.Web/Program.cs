using Gbrindes.Web.Authentication;
using Gbrindes.Web.Identity;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Gbrindes.Web.Imports;
using Gbrindes.Web.Protocols;
using Gbrindes.Web.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var isTesting = builder.Environment.IsEnvironment("Testing");

// Add services to the container.
builder.Services.AddControllersWithViews().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var invalidJson = context.ModelState.ContainsKey("$");
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = invalidJson ? StatusCodes.Status400BadRequest : StatusCodes.Status422UnprocessableEntity
        };
        return invalidJson ? new BadRequestObjectResult(problem) : new UnprocessableEntityObjectResult(problem);
    };
});
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
if (!builder.Environment.IsDevelopment() && !isTesting)
{
    var allowedHosts = builder.Configuration["AllowedHosts"]?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Configure the production PostgreSQL connection before starting the application.");
    }

    if (allowedHosts is null || allowedHosts.Length == 0 || allowedHosts.Contains("*", StringComparer.Ordinal))
    {
        throw new InvalidOperationException("Configure explicit production hostnames before starting the application.");
    }

    var publicBaseUrl = builder.Configuration["DeliveryVerification:PublicBaseUrl"];
    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicVerificationUri)
        || !string.Equals(publicVerificationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || publicVerificationUri.IsLoopback
        || !allowedHosts.Contains(publicVerificationUri.Host, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Configure an HTTPS delivery verification URL whose hostname is listed in AllowedHosts.");
    }

    var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
    if (!Path.IsPathFullyQualified(dataProtectionKeysPath ?? string.Empty))
    {
        throw new InvalidOperationException("Configure an absolute persistent DataProtection:KeysPath before starting the application.");
    }
}

var dataProtection = builder.Services.AddDataProtection();
var configuredDataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(configuredDataProtectionPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(configuredDataProtectionPath));

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<TradeReceivingService>();
builder.Services.AddScoped<TradeWithdrawalService>();
builder.Services.AddScoped<InternalRequestDeliveryService>();
builder.Services.AddScoped<StockTransferService>();
builder.Services.AddScoped<StockExitOrderService>();
builder.Services.AddScoped<StockReconciliationService>();
builder.Services.AddScoped<EventWorkflowService>();
builder.Services.AddScoped<EventWithdrawalService>();
builder.Services.AddScoped<EventReturnService>();
builder.Services.AddSingleton<ItemPhotoStorage>();
builder.Services.AddSingleton<BrandingLogoStorage>();
builder.Services.AddSingleton<DigitalSignatureStorage>();
builder.Services.AddSingleton<DeliveryPdfGenerator>();
builder.Services.AddSingleton<DeliveryVerificationService>();
builder.Services.AddSingleton<TradeInvoiceStorage>();
builder.Services.AddSingleton<StockAttachmentStorage>();
builder.Services.AddSingleton<ImportTemplateService>();
builder.Services.AddScoped<ImportPreviewService>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailOutboxProcessor>();
if (builder.Configuration.GetValue("Email:OutboxEnabled", false) && !isTesting)
    builder.Services.AddHostedService<EmailOutboxWorker>();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AccessTokenDefaults.Scheme;
        options.DefaultChallengeScheme = AccessTokenDefaults.Scheme;
    })
    .AddScheme<AuthenticationSchemeOptions, AccessTokenAuthenticationHandler>(AccessTokenDefaults.Scheme, _ => { });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isTesting ? int.MaxValue : 30,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in new[] { "lookups.view", "lookups.manage", "lookups.delete", "lookups.purge", "items.view", "items.manage", "items.delete", "items.purge", "stock.view", "stock.entry", "stock.exit", "stock.exit_confirm", "stock.adjust", "stock.transfer", "dashboard.view", "audit.view", "deliveries.view", "reports.view", "reports.export", "import.run", "requests.create", "requests.process", "requests.approve", "requests.cancel_any", "approval_rules.manage", "events.view", "events.manage", "events.withdraw", "settings.manage", "users.view", "users.manage", "roles.manage" })
    {
        options.AddPolicy(permission, policy =>
        {
            policy.RequireAuthenticatedUser();
            // Preserve the PHP source's scoped admin bypass without creating or merging permission claims.
            if (permission is "requests.approve" or "requests.process" or "approval_rules.manage" or "deliveries.view" or "events.view" or "events.manage" or "settings.manage" or "users.view" or "users.manage")
                policy.RequireAssertion(context => context.User.IsInRole("admin") || context.User.HasClaim("permission", permission));
            else
                policy.RequireClaim("permission", permission);
        });
    }
    options.AddPolicy("stock.transfer_or_exit", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => context.User.IsInRole("admin")
            || context.User.HasClaim("permission", "stock.transfer")
            || context.User.HasClaim("permission", "stock.exit"));
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
        return Task.CompletedTask;
    });

    await next();
});
app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
