using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tanba.Scanner;
using Tanba.Shell;
using Tanba.Storage;
using Tanba.Update;

namespace Tanba;

/// <summary>
/// Первый запуск: пять шагов, от приветствия до готового каталога.
///
/// Живёт отдельно от обычного запуска и до него: обычный первым делом
/// открывает базу, а открывать нечего, пока не сказано где. Поэтому здесь
/// свой маленький сервер, своё окно и свой кусок работы по заведению
/// хранилища. Вернув Config, уступает место обычному запуску в том же
/// процессе: перезапуск был бы хуже, единственная копия держит мьютекс.
/// </summary>
public static class Setup
{
    // Ход подготовки. Читает страница, пишет фоновый поток, поэтому volatile.
    private static volatile string _phase = "";
    private static volatile bool _adopted;
    private static int _scanned, _total;
    private static volatile string _failed = "";

    public static async Task<Config?> Ask(string? current, int port)
    {
        var picked = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "ui"),
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate",
        });

        app.MapGet("/api/setup", () => Results.Ok(new
        {
            current,
            // Диск мог не успеть подключиться: тогда приветствие пропускаем
            // и открываемся сразу на выборе места, человек уже знает программу.
            waiting = current is not null && !Directory.Exists(current),
            version = Updater.Version,
            // Чем открыться. Настроек ещё нет, спросить некого, поэтому берём
            // язык Windows: на русской системе мастер сразу русский.
            lang = Lang.OfWindows(),
            autostart = Startup.IsOn(),
            shortcut = File.Exists(DesktopLink),
            found = RootStore.Suggest().Select(p => new
            {
                path = p,
                files = RootStore.Inspect(p).Files,
                created = Created(p),
            }),
        }));

        app.MapPost("/api/setup/look", (PathCmd c) => Results.Ok(Say(RootStore.Inspect(c.Path))));

        app.MapPost("/api/setup/prepare", (PrepareCmd c) =>
        {
            var look = RootStore.Inspect(c.Path);
            if (look.Kind == RootStore.Kind.Bad) return Results.BadRequest(Say(look));

            _adopted = look.Kind == RootStore.Kind.Tanba;
            _phase = "dirs";
            _failed = "";
            _ = Task.Run(() => Prepare(look.Path, c.Shortcut, c.Autostart, Lang.Pick(c.Lang)));
            return Results.Ok(new { ok = true, adopted = _adopted });
        });

        app.MapGet("/api/setup/progress", () => Results.Ok(new
        {
            phase = _phase,
            adopted = _adopted,
            scanned = Volatile.Read(ref _scanned),
            total = Volatile.Read(ref _total),
            failed = _failed,
        }));

        app.MapPost("/api/setup/finish", (PathCmd c) =>
        {
            picked.TrySetResult(c.Path);
            return Results.Ok(new { ok = true });
        });

        await app.StartAsync();

        // Окно на своём STA-потоке с настоящим циклом сообщений, как главное.
        // Самодельная прокрутка сообщений тут не годится: без цикла WinForms
        // продолжения await уходят в пул потоков, и WebView2 молча падает на
        // обращении из чужого потока, оставляя чёрное окно.
        var ui = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var win = new SetupWindow(port);

            picked.Task.ContinueWith(_ =>
            {
                try { win.BeginInvoke(new Action(win.Close)); }
                catch (Exception) { /* окно уже закрыто */ }
            });

            Application.Run(win);
            picked.TrySetResult(null);
        })
        { Name = "Tanba setup" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        var chosen = await picked.Task;
        ui.Join();
        await app.StopAsync();
        await app.DisposeAsync();

        return chosen is null ? null : Accept(chosen);
    }

    /// <summary>
    /// Настоящая работа шага «Готовлю»: папки, база, обход приёма.
    /// Превью здесь нет намеренно: их рисует Windows по требованию, когда
    /// плитка попадает на экран, и показывать несуществующий шаг нельзя.
    /// </summary>
    private static void Prepare(string root, bool shortcut, bool autostart, string lang)
    {
        try
        {
            var cfg = new Config(root);
            cfg.RenameOldInbox();
            cfg.EnsureLayout();

            _phase = "db";
            var db = new Db(cfg.DbPath);
            var fresh = db.EnsureSchema();
            db.Migrate();

            _phase = "scan";
            var repo = new Repo(db);
            InboxMove.Carry(cfg, repo);

            // Язык, выбранный на первом шаге, с этой минуты живёт в настройках.
            // Новой базе он же задаёт язык стартовых тегов, и только ей: теги
            // это данные, и переключение интерфейса их потом не трогает.
            new Prefs(repo).Set(Prefs.Lang, lang);
            if (fresh) Seed.Starter(repo, lang);

            var thumbs = new Thumbs(cfg);
            var ingest = new Ingest(cfg, repo, thumbs);

            // Всего файлов считаем по диску, пройденных по базе: обход сам
            // о своём ходе не рассказывает, а эти два числа честны и без него.
            Volatile.Write(ref _total, OnDisk(cfg));
            Volatile.Write(ref _scanned, 0);

            var scan = Task.Run(() => ingest.ScanInbox());
            while (!scan.IsCompleted)
            {
                Volatile.Write(ref _scanned, InBase(cfg));
                Thread.Sleep(250);
            }
            scan.GetAwaiter().GetResult();
            Volatile.Write(ref _scanned, InBase(cfg));

            _phase = "extras";
            Startup.Set(autostart);
            SetShortcut(shortcut);

            _phase = "done";
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Подготовка не удалась: {e}");
            _failed = e.Message;
            _phase = "failed";
        }
    }

    private static int OnDisk(Config cfg)
    {
        try
        {
            return Directory.EnumerateFiles(cfg.Inbox, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Count();
        }
        catch (Exception) { return 0; }
    }

    private static int InBase(Config cfg)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            { DataSource = cfg.DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM files WHERE is_missing = 0 AND rel_path LIKE $p";
            cmd.Parameters.AddWithValue("$p", Config.InboxName + @"\%");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception) { return Volatile.Read(ref _scanned); }
    }

    // ── Ярлык на столе ───────────────────────────────────────────────────
    // Его создаёт установщик сам, поэтому галочка не создаёт, а убирает:
    // снял её значит удалили ярлык, поставил обратно значит вернули.

    private static string DesktopLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tanba.lnk");

    private static void SetShortcut(bool on)
    {
        try
        {
            if (on) return;                       // установщик уже положил
            if (File.Exists(DesktopLink)) File.Delete(DesktopLink);
        }
        catch (Exception e) { Console.Error.WriteLine($"Ярлык: {e.Message}"); }
    }

    private static string? Created(string root)
    {
        try
        {
            var db = Path.Combine(root, "_meta", "tanba.db");
            return File.Exists(db) ? File.GetCreationTime(db).ToString("dd.MM.yyyy") : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Превращает выбранный человеком путь в настройку программы.
    ///
    /// Отдельным шагом потому, что тут и была ошибка: путь становился
    /// настройкой, но нигде не запоминался, и мастер открывался при каждом
    /// запуске, хотя программа была установлена и настроена. Мимо этого шага
    /// пути от мастера к работающей программе нет, и он проверяется тестом.
    /// </summary>
    public static Config Accept(string chosen)
    {
        RootStore.Write(chosen);
        return new Config(chosen);
    }

    private static object Say(RootStore.Look l) => new
    {
        kind = l.Kind.ToString().ToLowerInvariant(),
        path = l.Path,
        files = l.Files,
        items = l.Items,
        note = l.Note,
        created = l.Kind == RootStore.Kind.Tanba ? Created(l.Path) : null,
    };

    public sealed record PathCmd(string Path);
    public sealed record PrepareCmd(string Path, bool Shortcut, bool Autostart, string? Lang);
}

/// <summary>
/// Окно первого запуска. Рамка системная, а не своя: это разовый разговор,
/// вкладок в нём нет, и городить ради него собственную шапку незачем.
/// </summary>
public sealed class SetupWindow : Form
{
    private readonly WebView2 _web = new();
    private readonly int _port;

    public SetupWindow(int port)
    {
        _port = port;
        Text = "Tanba";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(620, 460);
        BackColor = ColorTranslator.FromHtml("#0B0B0C");
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch (Exception) { /* без значка обойдёмся */ }

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = ColorTranslator.FromHtml("#0B0B0C");
        Controls.Add(_web);
    }

    /// Готовим WebView2 не в конструкторе, а когда окно уже показано: к этому
    /// моменту цикл сообщений WinForms работает, и продолжения await вернутся
    /// в этот же поток, а не в пул.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PaintFrame();
        _ = Boot();
    }

    // Шапку окна красит система, и по умолчанию она светлая: белая полоса
    // поверх почти чёрной страницы. Просим тёмную и задаём ей наш цвет,
    // те же ручки, что у главного окна, см. MainWindow.Chrome.cs.
    private const int DwmDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmBorderColor = 34;
    private const int Ink = 0x0C0B0B;   // #0B0B0C задом наперёд, как ждёт DWM

    private void PaintFrame()
    {
        if (!IsHandleCreated) return;
        var on = 1;
        var ink = Ink;
        // Старые сборки Windows этих свойств не знают и вернут ошибку.
        // Ничего не сломается, шапка останется системной.
        DwmSetWindowAttribute(Handle, DwmDarkMode, ref on, sizeof(int));
        DwmSetWindowAttribute(Handle, DwmCaptionColor, ref ink, sizeof(int));
        DwmSetWindowAttribute(Handle, DwmBorderColor, ref ink, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private async Task Boot()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tanba", "webview2");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _web.EnsureCoreWebView2Async(env);

        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnMessage;
        _web.Source = new Uri($"http://127.0.0.1:{_port}/setup.html");
    }

    /// <summary>Выбор папки умеет только форма: странице системный диалог недоступен.</summary>
    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? kind;
        try { kind = JsonDocument.Parse(e.WebMessageAsJson).RootElement.GetProperty("kind").GetString(); }
        catch (Exception) { return; }
        if (kind != "browse") return;

        using var dlg = new FolderBrowserDialog
        {
            Description = "Где держать архив Tanba",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var json = JsonSerializer.Serialize(new { kind = "folder", path = dlg.SelectedPath });
        try { _web.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception) { /* окно уже закрыли */ }
    }
}
