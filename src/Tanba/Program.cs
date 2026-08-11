using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tanba;
using Tanba.Scanner;
using Tanba.Shell;
using Tanba.Storage;
using Tanba.Update;
using Tanba.Web;
using Velopack;

// Строго первой строкой. При установке, обновлении и удалении Velopack
// запускает этот же exe со служебными ключами: он отрабатывает их здесь
// и завершает процесс. Любой код выше молотил бы впустую в эти моменты,
// а обращение к диску S: там ещё и упало бы, потому что его может не быть.
VelopackApp.Build().Run();

// Сразу после него и до всего остального: с этого места всё, что программа
// говорит через Console, уходит в файл, см. Log.cs. Своего окна у неё нет.
Log.Start();

var toTray = args.Contains(Args.Tray);

// Вторая копия не нужна и вредна: порт один и база одна. После включения
// автозапуска программа всегда висит в трее, и ярлык на столе иначе
// плодил бы копии, дерущиеся за один и тот же порт.
using var single = new Mutex(true, @"Local\Tanba.instance", out var firstCopy);
if (!firstCopy)
{
    await Poke();
    return;
}

var cfg = Config.Load();

// При запуске вместе с Windows диск может быть ещё не подключён, поэтому ждём.
// Терпения даём больше именно в этом режиме: человек в этот момент не смотрит.
if (cfg is not null &&
    !await WaitForRoot(cfg, toTray ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(10)))
{
    Console.Error.WriteLine($"Хранилище недоступно: {cfg.Root}");
    cfg = null;
}

// Места нет или оно не отозвалось: спрашиваем, а не выходим с сообщением
// об ошибке. Раньше здесь был тупик: окошко «подключи диск и запусти снова»
// и конец, даже если человек хотел просто выбрать другое место.
if (cfg is null)
{
    cfg = await Setup.Ask(Config.Load()?.Root, 5577);
    if (cfg is null) { Console.WriteLine("Место не выбрано, выходим."); return; }
    Console.WriteLine($"Выбрано хранилище: {cfg.Root}");
}

cfg.EnsureLayout();

var db = new Db(cfg.DbPath);
if (db.EnsureSchema()) Console.WriteLine($"База создана: {cfg.DbPath}");
db.Migrate();

var repo = new Repo(db);
var prefs = new Prefs(repo);
var updater = new Updater(prefs);
var thumbs = new Thumbs(cfg);
var ingest = new Ingest(cfg, repo, thumbs);
var safety = new Safety(cfg, repo);
var watcher = new InboxWatcher(cfg, ingest);

// Сторож хранилища: узнаёт переложенные и переименованные мимо программы
// файлы по номеру тома и подбирает то, что положили в обход приёма.
var storeScan = new StoreScan(cfg, repo);
var storeWatch = new StoreWatcher(cfg, storeScan);
storeWatch.Done += r => Console.WriteLine($"Хранилище: {r}");

safety.BackupDaily();

Console.WriteLine($"Tanba {Updater.Version}, хранилище {cfg.Root}");

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
        // Первый пересчёт после запуска идёт в фоне и на большом приёме
        // занимает заметное время: экран должен сказать об этом, а не врать нулём.
        scanning = watcher.Scanning,
        inbox = inbox.Select(f => new
        {
            id = f.Id,
            name = f.OrigName,
            ext = f.Ext,
            size = f.Size,
            addedAt = f.AddedAt,
            // Дата самого файла, для панели сведений. Времени создания здесь
            // нет намеренно: в Windows это время появления копии, и на всех
            // файлах хранилища оно позже времени изменения.
            modifiedAt = f.Mtime,
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

// Обход хранилища руками. Обычно его запускает сторож сам, но кнопка нужна:
// после большой уборки в проводнике ждать затишья незачем.
app.MapPost("/api/storescan", () =>
{
    var r = storeScan.Run();
    return Results.Ok(new
    {
        followed = r.Followed, movedOut = r.MovedOut,
        lost = r.Lost, back = r.Back, adopted = r.Adopted, refused = r.Refused,
        tracked = r.Tracked, total = r.Total,
    });
});

// ── Библиотека ───────────────────────────────────────────────────────────
// Отбор по тегам, а не путь по папкам: теги равноправны, иерархии нет.

app.MapLibrary(repo, cfg);
app.MapCatalogs(repo);
app.MapFiles(repo, cfg, thumbs);
app.MapTagsAdmin(repo);
app.MapSettings(cfg, prefs, updater, () => watcher.Pending, () => watcher.Scanning);

// Сюда стучится второй запуск программы, чтобы вместо своей копии
// поднять уже работающее окно.
app.MapPost("/api/show", () =>
{
    Tanba.Host.MainWindow.ShowExisting();
    return Results.Ok(new { ok = true });
});

// ── Превью ───────────────────────────────────────────────────────────────
// Эскиз рисует сама Windows руками Corel и Adobe, см. Shell/Thumbs.cs.

app.MapGet("/api/thumb/{id:long}", async (HttpContext http, long id, int? size) =>
{
    // Без Cache-Control ответ с одним Last-Modified браузер держит у себя
    // столько, сколько сочтёт нужным, и сервер об этом даже не спрашивает.
    // Файл при этом мог смениться под тем же именем, а эскиз остался старый.
    // no-cache не запрещает хранить, а требует спросить: обычно это 304.
    http.Response.Headers.CacheControl = "no-cache";

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

if (args.Contains(Args.NoWindow))
{
    watcher.Start();
    storeWatch.Start();
    Console.WriteLine("Окно отключено. Открой http://127.0.0.1:5577");
    app.Run();
    return;
}

await app.StartAsync();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
Tanba.Host.MainWindow.Start(cfg, repo, 5577,
    onClosed: lifetime.StopApplication, watcher: watcher, startHidden: toTray);

// Обновление могло переложить файлы, а в реестре остался прежний путь.
Startup.Refresh();

// Пересчёт приёма только теперь и в фоне. Раньше он стоял в конструкторе
// наблюдателя и держал запуск, пока считался sha256 всей папки: на сотне
// файлов это полминуты, а с автозапуском столько же на каждой загрузке Windows.
watcher.Start();
storeWatch.Start();

Console.WriteLine(toTray ? "Поднялись в трей." : "Окно открыто.");

await app.WaitForShutdownAsync();

// ── Мелочи запуска ───────────────────────────────────────────────────────

/// <summary>Просит уже работающую копию показать своё окно.</summary>
static async Task Poke()
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await http.PostAsync("http://127.0.0.1:5577/api/show", null);
        Console.WriteLine("Tanba уже работает, показали её окно.");
    }
    catch (Exception)
    {
        // Та копия ещё поднимается или занята. Своё окно всё равно не открываем:
        // две копии на одну базу хуже, чем одна не откликнувшаяся.
        Console.WriteLine("Tanba уже работает.");
    }
}

/// <summary>Ждёт, пока появится корень хранилища.</summary>
static async Task<bool> WaitForRoot(Config cfg, TimeSpan limit)
{
    if (Directory.Exists(cfg.Root)) return true;

    Console.WriteLine($"Жду хранилище {cfg.Root}");
    var until = DateTime.UtcNow + limit;
    while (DateTime.UtcNow < until)
    {
        await Task.Delay(2000);
        if (Directory.Exists(cfg.Root)) return true;
    }
    return false;
}

static void Fail(string text)
{
    Console.Error.WriteLine(text);
    try
    {
        System.Windows.Forms.MessageBox.Show(text, "Tanba",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Warning);
    }
    catch (Exception) { /* некому показать, в консоли уже написано */ }
}

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
