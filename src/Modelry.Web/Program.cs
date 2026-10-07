using Serilog;
using Modelry.Core.Gateway;
using Modelry.Tridion;
using Modelry.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

var tridion = builder.Configuration.GetSection("Tridion").Get<TridionOptions>() ?? new TridionOptions();
builder.Services.Configure<TridionOptions>(builder.Configuration.GetSection("Tridion"));
builder.Services.Configure<TemplateFieldOptions>(builder.Configuration.GetSection("Templates"));
builder.Services.AddControllersWithViews(o => o.Filters.Add<RequireTridionSessionFilter>());
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddDistributedMemoryCache();
builder.Services.AddMemoryCache();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(tridion.SessionMinutes);
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.Name = ".Modelry.Session";
});
builder.Services.AddHttpClient<AccessManagementTokenClient>()
    .ConfigurePrimaryHttpMessageHandler(() => string.IsNullOrWhiteSpace(tridion.ProxyUrl)
        ? new HttpClientHandler()                                         // system proxy settings
        : new HttpClientHandler { Proxy = new System.Net.WebProxy(tridion.ProxyUrl, BypassOnLocal: true), UseProxy = true });
builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<InMemoryTridionGateway>();
builder.Services.AddScoped<TridionSession>();
builder.Services.AddScoped<WizardState>();
builder.Services.AddHttpClient<AemConnectionClient>();
builder.Services.AddScoped<GatewayAccessor>();
builder.Services.AddScoped<RequireTridionSessionFilter>();
builder.Services.AddSingleton<UploadStore>();
builder.Services.AddSingleton<ImportJobStore>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = tridion.MaxUploadMegabytes * 1024L * 1024L);

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseSession();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

if (tridion.AllowDemoMode)
    await DemoSeeder.SeedAsync(app.Services.GetRequiredService<InMemoryTridionGateway>(),
        Path.Combine(app.Environment.WebRootPath, "samples", "IA_Template.xlsx"), app.Logger);

app.Run();
