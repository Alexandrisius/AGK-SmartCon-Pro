// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

using SmartCon.Cloud.Api.Auth;
using SmartCon.Cloud.Api.Cas;
using SmartCon.Cloud.Api.Data;
using SmartCon.Cloud.Api.Domain;
using SmartCon.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CloudDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Pg")));

builder.Services.AddIdentityCore<CloudUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = true; // login-throttling/rate limit — C1 (ADR-076 §1)
    })
    .AddEntityFrameworkStores<CloudDbContext>();

builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<IObjectStorage, FileSystemObjectStorage>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Convert.FromHexString(builder.Configuration["Jwt:SigningKeyHex"]!)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

// Срез: EnsureCreated (EF-миграции в проде — C1). Retry-цикл: PG-контейнер может стартовать дольше API.
for (var attempt = 1; ; attempt++)
{
    try
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            await db.Database.EnsureCreatedAsync();
            // EnsureCreated не меняет схему существующей БД: tombstone-таблица
            // (появилась после первых деплоев) добирается идемпотентным DDL.
            // Имена колонок — PascalCase как в EF-маппинге (существующие таблицы тоже PascalCase).
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS unpublished_slugs (" +
                "\"Slug\" character varying(64) NOT NULL PRIMARY KEY, " +
                "\"UnpublishedAtUtc\" timestamp with time zone NOT NULL DEFAULT now())");
        }
        break;
    }
    catch (Npgsql.NpgsqlException) when (attempt < 30)
    {
        await Task.Delay(2000);
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapAuthEndpoints();
app.MapFileEndpoints();
app.MapCatalogEndpoints();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
