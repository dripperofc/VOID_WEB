using VoidServer.Hubs;

var builder = WebApplication.CreateBuilder(args);

// O Render injeta a variável PORT — se não existir, cai para 5000 (local)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// CORS — aceita qualquer origem para o cliente Avalonia conectar
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddSignalR();

var app = builder.Build();

app.UseCors("AllowAll");

app.MapHub<ChatHub>("/voidchat");

app.MapGet("/", () => "SERVER ONLINE");

// ══════════════════════════════════════════════════════
// HEALTH — ping real para o admin.html
// ══════════════════════════════════════════════════════
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// ══════════════════════════════════════════════════════
// STATS — contagem pública para o landing page
// ══════════════════════════════════════════════════════
app.MapGet("/stats", () =>
{
    var usersDir = Path.Combine("db", "users");
    var msgsDir  = Path.Combine("db", "messages");
    var total    = Directory.Exists(usersDir) ? Directory.GetFiles(usersDir, "*.json").Length : 0;
    var online   = ChatHub.OnlineCount;
    var msgs     = Directory.Exists(msgsDir)  ? Directory.GetFiles(msgsDir,  "*.json").Length : 0;
    return Results.Ok(new { total, online, messages = msgs });
});

// ══════════════════════════════════════════════════════
// ADMIN — rotas protegidas por header secret
// ══════════════════════════════════════════════════════
var adminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET") ?? "void_admin_local";

app.MapGet("/admin/users", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    var usersDir = Path.Combine("db", "users");
    if (!Directory.Exists(usersDir)) return Results.Ok(Array.Empty<object>());
    var users = new List<object>();
    foreach (var file in Directory.GetFiles(usersDir, "*.json"))
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var u = System.Text.Json.JsonSerializer.Deserialize<StoredUser>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (u != null) users.Add(new { u.Username, u.Nickname, u.AvatarColor, u.CreatedAt, Friends = u.Friends.Count, IsOnline = ChatHub.IsOnline(u.Username) });
        }
        catch { }
    }
    return Results.Ok(users);
});

app.MapGet("/admin/online", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    return Results.Ok(ChatHub.OnlineUsers);
});

app.MapGet("/admin/messages/dm", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    var msgsDir = Path.Combine("db", "messages");
    if (!Directory.Exists(msgsDir)) return Results.Ok(Array.Empty<object>());
    var all = new List<object>();
    foreach (var file in Directory.GetFiles(msgsDir, "*.json"))
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var msgs = System.Text.Json.JsonSerializer.Deserialize<List<StoredMessage>>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (msgs != null) all.AddRange(msgs.Select(m => (object)new { m.From, m.To, m.Content, m.SentAt }));
        }
        catch { }
    }
    return Results.Ok(all.OrderByDescending(m => ((dynamic)m).SentAt).Take(200));
});

app.MapGet("/admin/messages/channels", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    var channelsDir = Path.Combine("db", "channels");
    if (!Directory.Exists(channelsDir)) return Results.Ok(Array.Empty<object>());
    var all = new List<object>();
    foreach (var file in Directory.GetFiles(channelsDir, "*.json"))
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var msgs = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(json);
            if (msgs != null) all.AddRange(msgs.Cast<object>());
        }
        catch { }
    }
    return Results.Ok(all.TakeLast(200));
});

app.MapDelete("/admin/users/{username}", async (string username, HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    var path = Path.Combine("db", "users", $"{username.ToLower()}.json");
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    Console.WriteLine($"[ADMIN] Deleted user: {username}");
    return Results.Ok();
});

app.MapPost("/admin/kick/{username}", async (string username, HttpContext ctx) =>
{
    if (!IsAdmin(ctx, adminSecret)) return Results.Unauthorized();
    await ChatHub.KickUser(username.ToLower());
    return Results.Ok();
});

static bool IsAdmin(HttpContext ctx, string secret)
{
    ctx.Request.Headers.TryGetValue("X-Admin-Secret", out var val);
    return val == secret;
}

app.Run();