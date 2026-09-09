using System.Net;
using System.Text.Json;
using Dashboard.Api;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var options = HubOptions.Load(builder.Configuration);
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Parse(options.BindAddress), options.UiPort); k.Listen(IPAddress.Parse(options.BindAddress), options.IngestionPort); k.Limits.MaxRequestBodySize = 4 * 1024 * 1024; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict; foreach (var converter in Protocol.Json.Converters) o.SerializerOptions.Converters.Add(converter); });
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SqliteStateStore<HubState>(Path.Combine(options.DataDirectory, "hub.sqlite")));
builder.Services.AddSingleton<HubService>();
builder.Services.AddSingleton<UiAuthentication>();
builder.Services.AddSingleton<TunnelWorker>(); builder.Services.AddHostedService(s => s.GetRequiredService<TunnelWorker>());
builder.Services.AddSingleton<SnapshotWorker>(); builder.Services.AddHostedService(s => s.GetRequiredService<SnapshotWorker>());
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(AuthenticationRateLimits.Configure);
var app = builder.Build();
LegacyImporter.Import(app.Services.GetRequiredService<SqliteStateStore<HubState>>(), options.DataDirectory);
_ = app.Services.GetRequiredService<UiAuthentication>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var path = context.Request.Path.Value ?? "/"; var ingestion = context.Connection.LocalPort == options.IngestionPort;
        var collectorRoute = path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) || path.Equals("/health", StringComparison.OrdinalIgnoreCase);
        if (ingestion != collectorRoute) { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new ErrorResponse("Not found")); return; }
        if (!ingestion && options.UsesDevTunnelAuthentication && !app.Services.GetRequiredService<UiAuthentication>().Authorized(context.Request))
            throw new DomainException(403, "Use this host or its private dashboard tunnel");
        if (ingestion && context.Request.Headers.ContainsKey("Origin")) throw new DomainException(403, "Collector requests only");
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !context.Request.HasJsonContentType()) throw new DomainException(415, "JSON content type required");
        if (!ingestion && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) && !app.Services.GetRequiredService<UiAuthentication>().Authorized(context.Request)) throw new DomainException(401, "Dashboard sign-in required");
        if (!ingestion && path.StartsWith("/openapi/", StringComparison.OrdinalIgnoreCase) && !app.Services.GetRequiredService<UiAuthentication>().Authorized(context.Request)) throw new DomainException(401, "Dashboard sign-in required");
        await next(context);
    }
    catch (DomainException error) { context.Response.StatusCode = error.Status; await context.Response.WriteAsJsonAsync(new ErrorResponse(error.Message)); }
    catch (BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; await context.Response.WriteAsJsonAsync(new ErrorResponse("Invalid request")); }
    catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new ErrorResponse("Invalid JSON")); }
});
app.UseRateLimiter();
app.MapGet("/api/auth/status", (HttpContext c, UiAuthentication auth) => new AuthInfo(auth.Authorized(c.Request), auth.RequiresLogin));
app.MapPost("/api/auth/login", (LoginInput input, HttpContext c, UiAuthentication auth) =>
{
    if (!auth.RequiresLogin) return new AuthInfo(auth.Authorized(c.Request), false);
    if (!auth.Login(input.AccessKey)) throw new DomainException(401, "Invalid access key");
    c.Response.Cookies.Append(UiAuthentication.CookieName, auth.Issue(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps || c.Request.Headers["X-Forwarded-Proto"] == "https", MaxAge = TimeSpan.FromDays(7), Path = "/" });
    return new AuthInfo(true, true);
}).RequireRateLimiting("dashboard-login");
app.MapPost("/api/auth/logout", (HttpContext c, UiAuthentication auth) => { auth.Logout(c.Request); c.Response.Cookies.Delete(UiAuthentication.CookieName); return new AuthInfo(!auth.RequiresLogin && auth.Authorized(c.Request), auth.RequiresLogin); });
app.MapGet("/api/status", (HubService hub) => hub.Snapshot());
app.MapGet("/api/tunnel", (TunnelWorker tunnels) => tunnels.Ui);
app.MapGet("/api/tunnels", (TunnelWorker tunnels) => new TunnelStates(tunnels.Ui, tunnels.Ingestion));
app.MapPut("/api/settings", (SettingsUpdate input, HubService hub) => hub.UpdateSettings(input));
app.MapPost("/api/devices/pair", (HubService hub, TunnelWorker tunnels) => hub.Pair(options.PublicIngestionUrl ?? tunnels.Ingestion.Url, options.IngestionTunnel.Id));
app.MapDelete("/api/devices/{id}", (string id, HubService hub) => hub.Revoke(id));
app.MapPost("/api/tasks", (TaskInput input, HubService hub) => hub.CreateTask(input));
app.MapPatch("/api/tasks/{id}", (string id, TaskUpdate input, HubService hub) => hub.UpdateTask(id, input));
app.MapPost("/api/tasks/{id}/heartbeat", (string id, HeartbeatInput input, HubService hub) => hub.UpdateTask(id, new(Dashboard.Contracts.TaskStatus.Running, LatestOutput: input.LatestOutput, LeaseMinutes: input.LeaseMinutes)));
app.MapDelete("/api/completions", (HubService hub) => hub.Clear(null));
app.MapDelete("/api/completions/{id}", (string id, HubService hub) => hub.Clear(id));
app.MapGet("/health", () => new HealthResponse("agent-dashboard-ingestion"));
app.MapPost("/v1/devices/register", (EnrollmentRequest input, HubService hub) => hub.Enroll(input)).RequireRateLimiting("device-enrollment");
string? Token(HttpContext c) => c.Request.Headers.Authorization.ToString() is var h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..] : null;
app.MapPost("/v1/devices/{id}/sessions", (string id, HttpContext c, HubService hub) => hub.OpenSession(id, Token(c)));
app.MapPut("/v1/devices/{id}/snapshot", (string id, DeviceReport report, HttpContext c, HubService hub) => hub.Report(id, Token(c), report));
app.MapGet("/api/events", async (HttpContext context, SnapshotWorker worker, UiAuthentication auth) =>
{
    context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    var (id, reader) = worker.Subscribe(); var token = context.RequestAborted;
    try
    {
        while (!token.IsCancellationRequested && auth.Authorized(context.Request))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(15));
            try { var item = await reader.ReadAsync(wait.Token); if (!auth.Authorized(context.Request)) break; await context.Response.WriteAsync($"event: {item.Name}\ndata: {item.Json}\n\n", token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { await context.Response.WriteAsync(": keep-alive\n\n", token); }
            await context.Response.Body.FlushAsync(token);
        }
    }
    catch (OperationCanceledException) { }
    finally { worker.Unsubscribe(id); }
});
app.MapOpenApi();
if (Directory.Exists(options.WebRoot))
{
    var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(options.WebRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}
app.MapGet("/", () => File.Exists(Path.Combine(options.WebRoot, "index.html"))
    ? Results.File(Path.GetFullPath(Path.Combine(options.WebRoot, "index.html")), "text/html")
    : Results.Text("Build the React UI with npm run build --prefix web.", "text/plain"));
app.Run();
public partial class Program;
