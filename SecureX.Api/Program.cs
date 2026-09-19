using System.Text;
using Amazon.SimpleSystemsManagement;
using Amazon.CloudWatchLogs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SecureX.Api.Data;
using SecureX.Api.Security;
using SecureX.Api.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
});

// Accept Expect: 100-continue from Ozow webhook client without returning 417
builder.WebHost.ConfigureKestrel(k =>
{
    k.AllowResponseHeaderCompression = false;
    k.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.AllowSynchronousIO = true);

// Disable file watchers before any config sources are built — prevents inotify exhaustion on Render
#pragma warning disable ASP0013 // Intentional: disable file watchers before configuration is built.
builder.Host.ConfigureAppConfiguration((_, config) =>
{
    foreach (var s in config.Sources.OfType<Microsoft.Extensions.Configuration.FileConfigurationSource>())
        s.ReloadOnChange = false;
});
#pragma warning restore ASP0013

// ── Config from env vars (override appsettings) ──────────────────────────────
builder.Configuration.AddEnvironmentVariables();

// Map flat env var names to config keys
var cfg = builder.Configuration;
cfg["Ozow:AccessToken"]                = cfg["OZOW_ACCESS_TOKEN"] ?? cfg["Ozow:AccessToken"];
cfg["Ozow:ApiKey"]                     = cfg["OZOW_API_KEY"] ?? cfg["Ozow:ApiKey"];
cfg["Ozow:AccountNumberDecryptionKey"] = cfg["OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY"] ?? cfg["Ozow:AccountNumberDecryptionKey"];
cfg["SmileId:BiometricProduct"] = cfg["SMILEID_BIOMETRIC_PRODUCT"] ?? cfg["SmileId:BiometricProduct"];
cfg["SmileId:EnhancedProduct"]  = cfg["SMILEID_ENHANCED_PRODUCT"]  ?? cfg["SmileId:EnhancedProduct"];
cfg["SmileId:Country"]          = cfg["SMILEID_COUNTRY"]           ?? cfg["SmileId:Country"];
cfg["SmileId:IdType"]           = cfg["SMILEID_ID_TYPE"]           ?? cfg["SmileId:IdType"];
cfg["SmileId:IdSelection"]      = cfg["SMILEID_ID_SELECTION"]      ?? cfg["SmileId:IdSelection"];
cfg["SmileId:SandboxIdNumber"]  = cfg["SMILEID_SANDBOX_ID_NUMBER"] ?? cfg["SmileId:SandboxIdNumber"];
cfg["Ozow:SiteCode"]                   = cfg["OZOW_SITE_CODE"] ?? cfg["Ozow:SiteCode"];
cfg["Ozow:PrivateKey"]                 = cfg["OZOW_PRIVATE_KEY"] ?? cfg["Ozow:PrivateKey"];
cfg["Ozow:PayoutApiKey"]               = cfg["OZOW_PAYOUT_API_KEY"] ?? cfg["Ozow:PayoutApiKey"];
cfg["Ozow:PayoutBaseUrl"]              = cfg["OZOW_PAYOUT_BASE_URL"] ?? cfg["Ozow:PayoutBaseUrl"];
cfg["Ozow:NotifyUrl"]                  = cfg["OZOW_NOTIFY_URL"] ?? cfg["Ozow:NotifyUrl"];
cfg["Ozow:VerifyUrl"]                  = cfg["OZOW_VERIFY_URL"] ?? cfg["Ozow:VerifyUrl"];
cfg["Ozow:CollectionNotifyUrl"]        = cfg["OZOW_COLLECTION_NOTIFY_URL"] ?? cfg["Ozow:CollectionNotifyUrl"];
    cfg["Ozow:CollectionBaseUrl"]          = cfg["OZOW_COLLECTION_BASE_URL"] ?? cfg["Ozow:CollectionBaseUrl"];
    cfg["Ozow:BankRefPrefix"]              = cfg["OZOW_BANK_REF_PREFIX"] ?? cfg["Ozow:BankRefPrefix"];
cfg["Ozow:IsTest"]                     = cfg["OZOW_IS_TEST"] ?? cfg["Ozow:IsTest"] ?? "false";
cfg["Ozow:OneApiClientId"]             = cfg["OZOW_ONE_API_CLIENT_ID"] ?? cfg["Ozow:OneApiClientId"];
cfg["Ozow:OneApiClientSecret"]         = cfg["OZOW_ONE_API_CLIENT_SECRET"] ?? cfg["Ozow:OneApiClientSecret"];
cfg["Ozow:OneApiBaseUrl"]              = cfg["OZOW_ONE_API_BASE_URL"] ?? cfg["Ozow:OneApiBaseUrl"];
cfg["Ozow:ReturnUrl"]                  = cfg["OZOW_RETURN_URL"] ?? cfg["Ozow:ReturnUrl"];
cfg["ConnectionStrings:Default"]       = cfg["DATABASE_URL"] ?? cfg["ConnectionStrings:Default"];
cfg["SmileId:PartnerId"]               = cfg["SMILEID_PARTNER_ID"] ?? cfg["SmileId:PartnerId"];
cfg["SmileId:ApiKey"]                  = cfg["SMILEID_API_KEY"] ?? cfg["SmileId:ApiKey"];
cfg["SmileId:BaseUrl"]                 = cfg["SMILEID_BASE_URL"] ?? cfg["SmileId:BaseUrl"];
cfg["SmileId:CallbackUrl"]             = cfg["SMILEID_CALLBACK_URL"] ?? cfg["SmileId:CallbackUrl"];
cfg["SmileId:PolicyUrl"]               = cfg["SMILEID_POLICY_URL"] ?? cfg["SmileId:PolicyUrl"];
cfg["Ses:FromAddress"]  = cfg["SES_FROM_ADDRESS"]  ?? "noreply@secureexchange.co.za";
cfg["Ses:FromName"]     = cfg["SES_FROM_NAME"]     ?? "SecureX";
cfg["Ses:ReplyTo"]      = cfg["SES_REPLY_TO"]      ?? "info@secureexchange.co.za";
cfg["Ses:AdminEmail"]   = cfg["SES_ADMIN_EMAIL"]   ?? "info@secureexchange.co.za";
cfg["Ses:FrontendBase"] = cfg["SES_FRONTEND_BASE"] ?? "https://www.secureexchange.co.za";
cfg["DealToken:Secret"]     = cfg["DEAL_TOKEN_SECRET"]     ?? cfg["DealToken:Secret"];
cfg["DealToken:ExpiryDays"] = cfg["DEAL_TOKEN_EXPIRY_DAYS"] ?? cfg["DealToken:ExpiryDays"] ?? "7";


// ── Database ─────────────────────────────────────────────────────────────────
var rawConnStr = builder.Configuration["ConnectionStrings:Default"] ?? "";
var connStr = rawConnStr.StartsWith("postgresql://") || rawConnStr.StartsWith("postgres://")
    ? ConvertUriToNpgsql(rawConnStr)
    : rawConnStr;
var migrationsAssembly = typeof(AppDbContext).Assembly.GetName().Name
    ?? throw new InvalidOperationException("Could not determine the SecureX EF migrations assembly.");

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseNpgsql(connStr, npg =>
    {
        npg.MigrationsAssembly(migrationsAssembly);
        npg.EnableRetryOnFailure(3);
    });
});

// ── JWT Auth ─────────────────────────────────────────────────────────────────
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? builder.Configuration["JWT_SECRET"]
    ?? throw new InvalidOperationException("JWT_SECRET env var is required");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        };
    });
builder.Services.AddAuthorization();

// ── DataProtection — persist keys to SSM in production (AWS only) ───────────
if (!builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_REGION")))
{
    builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
    builder.Services.AddAWSService<IAmazonSimpleSystemsManagement>();
    builder.Services.AddDataProtection()
        .PersistKeysToAWSSystemsManager("/securex/dataprotection");
}

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<HashService>();
builder.Services.AddScoped<DealReferenceService>();
builder.Services.AddScoped<OzowCollectionService>();
builder.Services.AddScoped<OzowPayoutService>();
builder.Services.AddScoped<SmileIdService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddAWSService<Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2>();
builder.Services.AddAWSService<IAmazonCloudWatchLogs>();
builder.Services.AddScoped<AwsCloudWatchLogsService>();
builder.Services.AddScoped<SystemFailureLogService>();
builder.Services.AddSingleton<DealTokenService>();
builder.Services.AddHostedService<ReconciliationService>();
builder.Services.AddHostedService<OzowPayoutPollerService>();
builder.Services.AddHostedService<InspectionWindowExpiryService>();
builder.Services.AddHttpClient("OzowPayout");
builder.Services.AddHttpClient("OzowCollection");
builder.Services.AddHttpClient("SmileId");

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = null;
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            []
        }
    });
});

// ── CORS ─────────────────────────────────────────────────────────────────────
var defaultAllowedOrigins = builder.Environment.IsDevelopment()
    ? "http://localhost:4200,http://localhost:3000,http://localhost:59144"
    : builder.Environment.IsStaging()
        ? "https://securex-staging.web.app,https://securex-staging.firebaseapp.com,https://securex-fe.web.app,https://securex.co.za,http://localhost:4200,http://localhost:3000"
        : "https://www.secureexchange.co.za,https://secureexchange.co.za,https://securex-fe.web.app,https://securex.co.za,https://securex-staging.web.app,https://securex-staging.firebaseapp.com,http://localhost:4200,http://localhost:3000";

var configuredAllowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");

var allowedOrigins = string.Join(",", new[]
{
    defaultAllowedOrigins,
    configuredAllowedOrigins
})
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
// Strip Expect: 100-continue — Ozow sends this on webhook POSTs; Kestrel returns 417 otherwise
app.Use(async (ctx, next) =>
{
    ctx.Request.Headers.Remove("Expect");
    await next();
});
app.UseMiddleware<AccessTokenMiddleware>();
app.UseCors();
app.UseMiddleware<SecureX.Api.Middleware.RequestLoggingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapControllers();

// ── Auto-migrate on startup ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var availableMigrations = db.Database.GetMigrations().ToArray();
    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToArray();

    app.Logger.LogInformation(
        "EF migrations discovered: {Count}; pending before startup migration: {PendingCount}; latest: {LatestMigration}",
        availableMigrations.Length,
        pendingMigrations.Length,
        availableMigrations.LastOrDefault() ?? "(none)");

    if (pendingMigrations.Length > 0)
    {
        app.Logger.LogInformation(
            "Applying pending EF migrations: {PendingMigrations}",
            string.Join(", ", pendingMigrations));
    }

    await db.Database.MigrateAsync();

    // Immutable audit log trigger — no UPDATE or DELETE allowed
    await db.Database.ExecuteSqlRawAsync(@"
        CREATE OR REPLACE FUNCTION prevent_audit_mutation()
        RETURNS TRIGGER LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'audit_logs are immutable';
        END;
        $$;

        DROP TRIGGER IF EXISTS trg_audit_immutable ON audit_logs;
        CREATE TRIGGER trg_audit_immutable
        BEFORE UPDATE OR DELETE ON audit_logs
        FOR EACH ROW EXECUTE FUNCTION prevent_audit_mutation();
    ");
}

app.Run();

static string ConvertUriToNpgsql(string uri)
{
    var u = new Uri(uri);
    var userInfo = u.UserInfo.Split(':');
    var db = u.AbsolutePath.TrimStart('/');
    var query = System.Web.HttpUtility.ParseQueryString(u.Query);
    var sslMode = query["sslmode"] ?? "require";
    return $"Host={u.Host};Port={u.Port};Database={db};Username={userInfo[0]};Password={userInfo[1]};SSL Mode={sslMode};Trust Server Certificate=true";
}
