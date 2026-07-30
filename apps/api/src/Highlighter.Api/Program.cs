using Highlighter.Api;
using Highlighter.Api.Endpoints;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

var apiOptions = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new();
var pipelineOptions = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new();
var layout = new RepoLayout(pipelineOptions);

Directory.CreateDirectory(layout.ApiLogDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(layout.ApiLogDir, "api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddSingleton(apiOptions);
builder.Services.AddSingleton(pipelineOptions);
builder.Services.AddSingleton(layout);
builder.Services.AddHttpClient(SupabaseDb.HttpClientName);
builder.Services.AddSingleton(provider => SupabaseDb.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseDb>>()));
builder.Services.AddHttpClient(SupabaseStorage.HttpClientName);
builder.Services.AddHttpClient(EditorExportService.HttpClientName,
    client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton(provider => SupabaseStorage.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseStorage>>()));
builder.Services.AddSingleton<EditorRenderer>();
builder.Services.AddSingleton<EditorExportService>();
builder.Services.AddSingleton<PipelineJobService>();
builder.Services.AddSingleton<SourceTitleService>();
builder.Services.AddSingleton<MediaCleanupScheduler>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MediaCleanupScheduler>());

// Auth: /api/auth proxies GoTrue; everything else validates the Supabase user
// JWT (ES256 against the project JWKS) when Api:RequireAuth is on.
builder.Services.AddHttpClient(SupabaseAuth.HttpClientName);
// The JWKS fetch runs synchronously inside the token-validation path, so it
// gets its own client with a hard 5s timeout instead of the default 100s.
builder.Services.AddHttpClient(SupabaseJwks.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton(provider => SupabaseAuth.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseAuth>>()));
builder.Services.AddSingleton(provider => SupabaseJwks.FromEnv(
    provider.GetRequiredService<IHttpClientFactory>(),
    provider.GetRequiredService<ILogger<SupabaseJwks>>()));
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL")?.TrimEnd('/');
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IServiceProvider>((options, services) =>
    {
        options.MapInboundClaims = false; // keep "sub" as "sub"
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidAudience = "authenticated",
            ValidAlgorithms = ["ES256"],
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                services.GetRequiredService<SupabaseJwks>().GetKeys(kid),
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(apiOptions.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));

// The auth endpoints are a password oracle and an account mint — throttle them
// per client address so a loop or a script can't hammer GoTrue.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }));
});

var app = builder.Build();

if (!apiOptions.RequireAuth)
    Log.Warning("Api:RequireAuth is OFF — every request is unscoped and unauthenticated. "
        + "This is a development-only mode.");
// Pre-warm the signing keys so the first login never waits on (or stampedes) a
// cold JWKS fetch.
_ = Task.Run(() => app.Services.GetRequiredService<SupabaseJwks>().GetKeys(null));

app.UseSerilogRequestLogging();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    // Known types surface their message; anything else gets a generic line so
    // internal details (paths, stack fragments, tool stderr) never reach clients.
    var (status, title, detail) = error switch
    {
        BadHttpRequestException bad => (bad.StatusCode, "Invalid request body",
            "The request body could not be read — check the JSON syntax and field types."),
        PostgrestException => (StatusCodes.Status502BadGateway, "Database request failed",
            "The database did not accept the request; try again in a moment."),
        WorkerUnavailableException => (StatusCodes.Status502BadGateway,
            "Pipeline worker unavailable", error.Message),
        JobConflictException => (StatusCodes.Status409Conflict, "Conflicting job", error.Message),
        OperationCanceledException => (499, "Request cancelled",
            "The client went away before the request finished."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred",
            "Something went wrong on our side — the details are in the server log."),
    };
    if (error is not null and not OperationCanceledException)
        Log.Error(error, "Request {Path} failed: {Message}", context.Request.Path, error.Message);
    if (context.Response.HasStarted) return; // mid-stream (SSE): nothing sane to write
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(
        new { type = "about:blank", title, status, detail },
        options: null, contentType: "application/problem+json");
}));
app.UseStatusCodePages();
app.UseCors();
// Full-quality local renders: Supabase storage caps objects (~50 MB), so the
// pipeline points oversized videos at this read-only mirror of outputs/.
// Range requests are supported, which is what lets the <video> tag seek.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(layout.OutputsRoot),
    RequestPath = "/media",
    ServeUnknownFileTypes = true,
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthEndpoints();
app.MapAuthEndpoints().RequireRateLimiting("auth");
IEndpointConventionBuilder[] gated =
[
    app.MapProjectEndpoints(),
    app.MapProjectActionEndpoints(),
    app.MapAgentChatEndpoints(),
    app.MapEditorEndpoints(),
    app.MapJobEndpoints(),
    app.MapAdminEndpoints(),
];
if (apiOptions.RequireAuth)
    foreach (var endpoints in gated) endpoints.RequireAuthorization();

app.Run();

public partial class Program;
