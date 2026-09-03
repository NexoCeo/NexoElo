using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using SaaS.Api.Middleware;
using SaaS.Api.Security;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Api.Hubs;
using SaaS.Domain.Enums;
using SaaS.Infrastructure;
using SaaS.Infrastructure.Configuration;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = ObterConfiguracaoObrigatoria(
    builder.Configuration,
    "ConnectionStrings:DefaultConnection");
builder.Configuration["ConnectionStrings:DefaultConnection"] =
    PostgresConnectionStringNormalizer.Normalize(connectionString);
builder.Services.AddInfrastructure();
var passwordRecoveryPepper = ObterConfiguracaoObrigatoria(
    builder.Configuration,
    "PasswordRecovery:Pepper");
if (Encoding.UTF8.GetByteCount(passwordRecoveryPepper) < 32)
    throw new InvalidOperationException("PasswordRecovery:Pepper deve ter pelo menos 32 bytes.");
var jwtIssuer = ObterConfiguracaoObrigatoria(builder.Configuration, "Jwt:Issuer");
var jwtAudience = ObterConfiguracaoObrigatoria(builder.Configuration, "Jwt:Audience");
var jwtKey = ObterConfiguracaoObrigatoria(builder.Configuration, "Jwt:Key");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("A configuracao Jwt:Key deve ter pelo menos 32 bytes.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var hasAuthorizationHeader = context.Request.Headers.Authorization
                    .ToString()
                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
                var accessToken = context.Request.Query["access_token"].ToString();
                var path = context.HttpContext.Request.Path;

                if (!hasAuthorizationHeader)
                {
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/agenda"))
                        context.Token = accessToken;
                    else if (context.Request.Cookies.TryGetValue(AuthCookie.Name, out var cookieToken))
                        context.Token = cookieToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddSignalR();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("password-recovery-email", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
    options.AddPolicy("password-recovery", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
    options.AddPolicy("registration", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
    options.AddPolicy("public-read", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    });


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.MapType<TipoUsuario>(() => new OpenApiSchema
    {
        Type = "string",
        Enum = Enum.GetNames<TipoUsuario>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });

    options.MapType<TipoUsuario?>(() => new OpenApiSchema
    {
        Type = "string",
        Nullable = true,
        Enum = Enum.GetNames<TipoUsuario>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });

    options.MapType<StatusAssinatura>(() => new OpenApiSchema
    {
        Type = "string",
        Enum = Enum.GetNames<StatusAssinatura>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });

    options.MapType<StatusAssinatura?>(() => new OpenApiSchema
    {
        Type = "string",
        Nullable = true,
        Enum = Enum.GetNames<StatusAssinatura>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });

    options.MapType<DiaFuncionamento>(() => new OpenApiSchema
    {
        Type = "string",
        Enum = Enum.GetNames<DiaFuncionamento>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });

    options.MapType<DiaFuncionamento?>(() => new OpenApiSchema
    {
        Type = "string",
        Nullable = true,
        Enum = Enum.GetNames<DiaFuncionamento>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray() ?? [];

if (allowedOrigins.Length == 0)
    throw new InvalidOperationException("Configure ao menos uma origem em Cors:AllowedOrigins.");

builder.Services.AddCors(options => options.AddPolicy("corsapp", policy =>
{
    policy
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
}));

var app = builder.Build();

await GarantirArmazenamentoUploads(app);
await GerarSlugsPendentes(app);

app.UseExceptionHandler();
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("corsapp");
app.UseMiddleware<AuthenticatedCookieOriginMiddleware>(allowedOrigins.AsEnumerable());
app.UseRateLimiter();

var uploadContentTypes = new FileExtensionContentTypeProvider();
uploadContentTypes.Mappings.Clear();
uploadContentTypes.Mappings[".jpg"] = "image/jpeg";
uploadContentTypes.Mappings[".jpeg"] = "image/jpeg";
uploadContentTypes.Mappings[".png"] = "image/png";
uploadContentTypes.Mappings[".webp"] = "image/webp";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "uploads")),
    RequestPath = "/uploads",
    ContentTypeProvider = uploadContentTypes,
    ServeUnknownFileTypes = false,
    OnPrepareResponse = context =>
        context.Context.Response.Headers.XContentTypeOptions = "nosniff"
});

app.UseMiddleware<MissingUploadFallbackMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<AgendaHub>("/hubs/agenda");
app.Run();

static string ObterConfiguracaoObrigatoria(IConfiguration configuration, string chave)
{
    var valor = configuration[chave];
    if (string.IsNullOrWhiteSpace(valor))
        throw new InvalidOperationException($"Configuracao obrigatoria ausente: {chave}.");

    return valor;
}

static async Task GerarSlugsPendentes(WebApplication app)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var usuarioRepository = scope.ServiceProvider.GetRequiredService<IUsuarioRepository>();
        var total = await usuarioRepository.GerarSlugsPendentes();

        if (total > 0)
            app.Logger.LogInformation("{Total} slug(s) de usuarios foram gerados automaticamente.", total);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Nao foi possivel gerar slugs pendentes automaticamente. Verifique se a coluna SLUG_USUARIO ja existe no banco.");
    }
}

static async Task GarantirArmazenamentoUploads(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var uploadService = scope.ServiceProvider.GetRequiredService<IArquivoUploadService>();
    await uploadService.GarantirEstruturaAsync();
}

