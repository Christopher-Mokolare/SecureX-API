using System.Text;
using Amazon.SimpleSystemsManagement;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SecureX.Api.Data;
using SecureX.Api.Security;
using SecureX.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Config from env vars (override appsettings) ──────────────────────────────
builder.Configuration.AddEnvironmentVariables();

// Map flat env var names to config keys
var cfg = builder.Configuration;
cfg["Ozow:AccessToken"]                = cfg["OZOW_ACCESS_TOKEN"] ?? cfg["Ozow:AccessToken"];
cfg["Ozow:ApiKey"]                     = cfg["OZOW_API_KEY"] ?? cfg["Ozow:ApiKey"];
cfg["Ozow:AccountNumberDecryptionKey"] = cfg["OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY"] ?? cfg["Ozow:AccountNumberDecryptionKey"];
cfg["Ozow:SiteCode"]                   = cfg["OZOW_SITE_CODE"] ?? cfg["Ozow:SiteCode"];
cfg["Ozow:PrivateKey"]                 = cfg["OZOW_PRIVATE_KEY"] ?? cfg["Ozow:PrivateKey"];
cfg["Ozow:PayoutApiKey"]               = cfg["OZOW_PAYOUT_API_KEY"] ?? cfg["Ozow:PayoutApiKey"];
cfg["Ozow:PayoutBaseUrl"]              = cfg["OZOW_PAYOUT_BASE_URL"] ?? cfg["Ozow:PayoutBaseUrl"];
cfg["Ozow:NotifyUrl"]                  = cfg["OZOW_NOTIFY_URL"] ?? cfg["Ozow:NotifyUrl"];
cfg["Ozow:CollectionNotifyUrl"]        = cfg["OZOW_COLLECTION_NOTIFY_URL"] ?? cfg["Ozow:CollectionNotifyUrl"];
cfg["Ozow:IsTest"]                     = cfg["OZOW_IS_TEST"] ?? cfg["Ozow:IsTest"] ?? "false";
cfg["Ozow:OneApiClientId"]             = cfg["OZOW_ONE_API_CLIENT_ID"] ?? cfg["Ozow:OneApiClientId"];
cfg["Ozow:OneApiClientSecret"]         = cfg["OZOW_ONE_API_CLIENT_SECRET"] ?? cfg["Ozow:OneApiClientSecret"];
cfg["Ozow:OneApiBaseUrl"]              = cfg["OZOW_ONE_API_BASE_URL"] ?? cfg["Ozow:OneApiBaseUrl"];
cfg["Ozow:ReturnUrl"]                  = cfg["OZOW_RETURN_URL"] ?? cfg["Ozow:ReturnUrl"];
cfg["ConnectionStrings:Default"]       = cfg["DATABASE_URL"] ?? cfg["ConnectionStrings:Default"];

// ── Database ─────────────────────────────────────────────────────────────────
var connStr = builder.Configuration["ConnectionStrings:Default"];
builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseNpgsql(connStr, npg =>
    {
        npg.EnableRetryOnFailure(3);
        if (Environment.GetEnvironmentVariable("DATABASE_SSL") != "false")
            npg.RemoteCertificateValidationCallback((_, _, _, _) => true);
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

// ── DataProtection — persist keys to SSM in production ──────────────────────
if (!builder.Environment.IsDevelopment())
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
builder.Services.AddScoped<TransactionService>();
builder.Services.AddHostedService<ReconciliationService>();
builder.Services.AddHttpClient("OzowPayout");
builder.Services.AddHttpClient("OzowOneApi");

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
var allowedOrigins = (Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "http://localhost:4200,http://localhost:3000,http://localhost:59144")
    .Split(',', StringSplitOptions.RemoveEmptyEntries);
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseMiddleware<AccessTokenMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapControllers();

// ── Auto-migrate on startup ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
