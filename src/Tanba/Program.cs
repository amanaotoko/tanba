using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tanba;
using Tanba.Scanner;
using Tanba.Shell;
using Tanba.Storage;
using Tanba.Web;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cfg = Config.Load();
cfg.EnsureLayout();

var db = new Db(cfg.DbPath);
if (db.EnsureSchema()) Console.WriteLine($"База создана: {cfg.DbPath}");
db.Migrate();

var repo = new Repo(db);
var thumbs = new Thumbs(cfg);
var ingest = new Ingest(cfg, repo, thumbs);
var safety = new Safety(cfg, repo);
var watcher = new InboxWatcher(cfg, ingest);

safety.BackupDaily();

Console.WriteLine($"Tanba, хранилище {cfg.Root}");
Console.WriteLine($"В приёме файлов: {watcher.Pending}");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "ui"),
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5577");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Иначе кириллица уезжает в \uXXXX и отладка превращается в мучение.
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseDefaultFiles();

// Кэш статики выключен намеренно. Файлы лежат на этой же машине, экономить
// нечего, а WebView2 держит свой кэш между запусками: правка стилей уезжала
// в него и окно показывало вчерашнюю вёрстку, хотя в браузере всё верно.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate",
});

// ── Состояние экрана разбора ─────────────────────────────────────────────

app.MapGet("/api/state", (string? sel) =>
{
    var selected = ParseIds(sel);
    using var c = repo.Open();

    var inbox = repo.ListInbox(c);
    var groups = repo.Groups(c);
    var states = repo.TagStates(c, selected);
    var (files, untagged, bytes) = repo.Stats(c);

    // На каком файле какие теги, чтобы карточки показывали свои чипы.
    var perFile = inbox.ToDictionary(f => f.Id, f => repo.TagsOf(c, f.Id));

    return Results.Ok(new
    {
        root = cfg.Root,
        pending = inbox.Count,
        inbox = inbox.Select(f => new
        {
            id = f.Id,
            name = f.OrigName,
            ext = f.Ext,
            size = f.Size,
            addedAt = f.AddedAt,
            tags = perFile[f.Id],
        }),
        groups = groups.Select(g => new
        {
            id = g.Id,
            name = g.Name,
            isMulti = g.IsMulti,
            isRequired = g.IsRequired,
            tags = g.Tags.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                count = t.FileCount,
                state = states.TryGetValue(t.Id, out var s) ? s.ToString().ToLowerInvariant() : "off",
            }),
        }),
        stats = new { files, untagged, bytes },
    });
});

// ── Разметка ─────────────────────────────────────────────────────────────

app.MapPost("/api/tag", (TagCmd cmd) =>
{
    using var c = repo.Open();
    using var tx = c.BeginTransaction();
    repo.ApplyTag(c, cmd.FileIds, cmd.TagId, cmd.On);
    tx.Commit();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/tags", (NewTag cmd) =>
{
    if (string.IsNullOrWhiteSpace(cmd.Name)) return Results.BadRequest(new { error = "пустое имя" });
    using var c = repo.Open();
    var id = repo.EnsureTag(c, cmd.GroupId, cmd.Name.Trim());
    return Results.Ok(new { id });
});

app.MapPost("/api/groups", (NewGroup cmd) =>
{
    if (string.IsNullOrWhiteSpace(cmd.Name)) return Results.BadRequest(new { error = "пустое имя" });
    using var c = repo.Open();
    var id = repo.EnsureGroup(c, cmd.Name.Trim(), cmd.IsMulti);
    return Results.Ok(new { id });
});

// ── Разложить ────────────────────────────────────────────────────────────

app.MapPost("/api/file", (FileCmd cmd) =>
{
    var res = ingest.File(cmd.FileIds, cmd.AllowUntagged);
    watcher.Rescan();
    safety.ExportCatalog();   // страховка обновляется вместе с каталогом
    return Results.Ok(new
    {
        moved = res.Moved,
        merged = res.MergedDuplicates,
        skipped = res.SkippedUntagged,
        errors = res.Errors,
        pending = watcher.Pending,
    });
});

app.MapPost("/api/rescan", () =>
{
    watcher.Rescan();
    return Results.Ok(new { pending = watcher.Pending });
});

// ── Библиотека ───────────────────────────────────────────────────────────
// Отбор по тегам, а не путь по папкам: теги равноправны, иерархии нет.

app.MapLibrary(repo, cfg);
app.MapCatalogs(repo);
app.MapFiles(repo, cfg, thumbs);

// ── Превью ───────────────────────────────────────────────────────────────
// Эскиз рисует сама Windows руками Corel и Adobe, см. Shell/Thumbs.cs.

app.MapGet("/api/thumb/{id:long}", async (long id, int? size) =>
{
    FileRow? f;
    long real;
    using (var c = repo.Open())
    {
        // У каталога своих байтов нет: показываем обложку, то есть лучший
        // растровый файл внутри. Если внутри пусто, эскиза не будет.
        using var cmd = c.Sql(
            "SELECT CASE WHEN kind = 'catalog' THEN cover_file_id ELSE id END FROM files WHERE id = $i",
            ("$i", id));
        real = cmd.ScalarLong();
        if (real == 0) return Results.NoContent();
        f = repo.Get(c, real);
    }
    if (f is null) return Results.NoContent();

    var full = cfg.ToFull(f.RelPath);
    var path = await Task.Run(() => thumbs.GetOrCreate(real, full, size ?? Thumbs.DefaultSize));
    return path is null ? Results.NoContent() : Results.File(path, "image/jpeg");
});

// ── Запуск ───────────────────────────────────────────────────────────────
// По умолчанию поднимаем своё окно: перетаскивание файлов наружу возможно
// только через системное CF_HDROP, в браузере оно принципиально недоступно.
// С ключом --no-window остаётся чистый сервер, открывается браузером.

if (args.Contains("--no-window"))
{
    Console.WriteLine("Окно отключено. Открой http://127.0.0.1:5577");
    app.Run();
    return;
}

await app.StartAsync();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
Tanba.Host.MainWindow.Start(cfg, repo, 5577, onClosed: lifetime.StopApplication, watcher: watcher);
Console.WriteLine("Окно открыто.");

await app.WaitForShutdownAsync();

static long[] ParseIds(string? s) =>
    string.IsNullOrWhiteSpace(s)
        ? []
        : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(x => long.TryParse(x, out _))
           .Select(long.Parse)
           .ToArray();

record TagCmd(long[] FileIds, long TagId, bool On);
record NewTag(long GroupId, string Name);
record NewGroup(string Name, bool IsMulti);
record FileCmd(long[] FileIds, bool AllowUntagged);
