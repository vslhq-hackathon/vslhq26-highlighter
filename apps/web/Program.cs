using Highlighter.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Highlighter.Web.Models;
using Highlighter.Web.Services;

// The studio agent's model credentials live in the monorepo root .env, the
// same file the pipeline uses.
Highlighter.Pipeline.Config.LoadEnv();

// Inline styles interpolate doubles ("width:12.5px"); a comma-decimal locale
// would emit invalid CSS and collapse the timeline.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
    System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
    System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Auth tokens sit in DataProtection-encrypted browser storage. In a container
// the default key ring is ephemeral — every restart would invalidate every
// session — so when a key directory is configured (a mounted volume in Azure),
// persist the keys there.
var dataProtectionKeysDir = builder.Configuration["DataProtection:KeysDir"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysDir))
{
    Directory.CreateDirectory(dataProtectionKeysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysDir));
}

// The Highlighter API (apps/api). Server-to-server: the browser only ever talks
// to this Blazor app; auth tokens live in DataProtection-encrypted local
// storage and Supabase keys never leave the API process.
var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5199";
builder.Services.AddHttpClient(AuthApiClient.HttpClientName,
    client => client.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddScoped<AuthApiClient>();
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<ApiClient>();

builder.Services.AddScoped<StudioState>();
builder.Services.AddScoped<IStudioBackend, ApiStudioBackend>();
builder.Services.AddScoped<StudioAgentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
