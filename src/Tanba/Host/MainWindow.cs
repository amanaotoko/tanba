using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tanba.Shell;
using Tanba.Storage;

namespace Tanba.Host;

/// <summary>
/// Окно программы: WebView2 на всю площадь плюс то, чего страница не умеет.
/// Перетащить файл в браузер или в папку можно только настоящим системным
/// перетаскиванием в формате CF_HDROP. Его ведёт форма, HTML тут бессилен.
/// </summary>
public sealed class MainWindow : Form
{
    /// Тот же фон, что у страницы: иначе окно на старте секунду белое.
    private const string BackHex = "#0B0B0C";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Config _cfg;
    private readonly Repo _repo;
    private readonly int _port;
    private readonly WebView2 _web = new();
    private readonly string _dragTmp;

    private int _batchNo;
    private string? _keepBatch;   // папка последнего перетаскивания
    private bool _dragging;
    private bool _accept;         // принимаем ли то, что сейчас тащат над окном

    public MainWindow(Config cfg, Repo repo, int port)
    {
        _cfg = cfg;
        _repo = repo;
        _port = port;
        _dragTmp = Path.Combine(cfg.Meta, "dragtmp");

        Text = "Tanba";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 560);
        BackColor = ColorTranslator.FromHtml(BackHex);

        // 1440×900, но не больше экрана: на ноутбуке окно иначе вылезает за края.
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
        ClientSize = new Size(Math.Min(1440, wa.Width - 40), Math.Min(900, wa.Height - 40));

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = ColorTranslator.FromHtml(BackHex);
        // Файлы извне ловит форма: странице HTML5-приём отдаёт содержимое, но не пути.
        _web.AllowExternalDrop = false;
        Controls.Add(_web);

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
    }

    /// <summary>
    /// Поднимает окно на собственном STA-потоке. OLE-перетаскивание работает
    /// только в STA, а точка входа с операторами верхнего уровня его не даёт.
    /// </summary>
    public static Thread Start(Config cfg, Repo repo, int port,
        Action? onClosed = null, Scanner.InboxWatcher? watcher = null)
    {
        var t = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var w = new MainWindow(cfg, repo, port);
            if (watcher is not null) w.AttachWatcher(watcher);
            Application.Run(w);
            onClosed?.Invoke();
        })
        { Name = "Tanba UI" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }

    // ── Запуск WebView2 ──────────────────────────────────────────────────

    /// <summary>
    /// Счётчик в трее. Не всплывашка на каждый файл: та задолбает за неделю
    /// и её выключат. Тихая цифра, которая мозолит глаз, работает годами.
    /// </summary>
    private Tray? _tray;
    private Scanner.InboxWatcher? _watcher;

    public void AttachWatcher(Scanner.InboxWatcher watcher)
    {
        _watcher = watcher;
        watcher.Changed += n =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => _tray?.Update(n)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        };
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _tray = new Tray(
            open: () => { Show(); WindowState = FormWindowState.Normal; Activate(); },
            rescan: () => _watcher?.Rescan(),
            quit: Close);
        if (_watcher is not null) _tray.Update(_watcher.Pending);

        // Ссылки от прошлого запуска: программу могли закрыть посреди перетаскивания.
        CleanDragTmp(null);

        try
        {
            // Своя папка данных: рядом с exe её класть некуда, туда может не быть записи.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tanba", "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось запустить WebView2.\n\n{ex.Message}\n\n" +
                "Нужен установленный WebView2 Runtime (Microsoft Edge WebView2).",
                "Tanba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        // Окно могли закрыть, пока поднимался движок.
        var w = _web.CoreWebView2;
        if (w is null || IsDisposed) return;

        w.WebMessageReceived += OnWebMessage;
        w.Settings.IsStatusBarEnabled = false;      // полоска ссылки внизу чужеродна в окне
        w.Settings.IsSwipeNavigationEnabled = false; // жест назад уводил бы со страницы

        _web.Source = new Uri($"http://127.0.0.1:{_port}/");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray?.Dispose();
        CleanDragTmp(null);
        base.OnFormClosed(e);
    }

    // ── Перетаскивание наружу ────────────────────────────────────────────

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        DragOutMsg? msg;
        try { msg = JsonSerializer.Deserialize<DragOutMsg>(e.WebMessageAsJson, Json); }
        catch (JsonException) { return; }

        if (msg is not { Kind: "dragOut", Ids.Length: > 0 }) return;

        string[] paths;
        try { paths = Materialize(msg.Ids); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось подготовить файлы к перетаскиванию: {ex.Message}");
            return;
        }
        if (paths.Length == 0) return;

        // DoDragDrop крутит собственный цикл сообщений и не возвращается до конца
        // перетаскивания, поэтому из обработчика сообщения его звать нельзя.
        BeginInvoke(() => StartDrag(paths));
    }

    /// <summary>
    /// Готовит пути для передачи наружу. В хранилище файл лежит как
    /// 000147__промо.mp4, а отдать надо промо.mp4, поэтому делаем жёсткую ссылку
    /// с чистым именем во временную папку. Байты не копируются.
    /// </summary>
    /// <summary>
    /// Каталог наружу отдаётся своим содержимым: байтов у него нет, а тащить
    /// работу целиком это ровно то, чего человек и ждёт. Вложенные каталоги
    /// разворачиваются тоже, порядок сохраняется.
    /// </summary>
    private static long[] Expand(Microsoft.Data.Sqlite.SqliteConnection c, long[] ids)
    {
        var outIds = new List<long>();
        var seen = new HashSet<long>();

        foreach (var id in ids)
        {
            using var cmd = c.Sql("""
                WITH RECURSIVE down(id) AS (
                  SELECT $i
                  UNION
                  SELECT ci.item_id FROM catalog_items ci JOIN down ON down.id = ci.catalog_id
                )
                SELECT f.id FROM files f JOIN down ON down.id = f.id
                WHERE f.kind = 'file' AND f.is_missing = 0
                """, ("$i", id));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fid = r.GetInt64(0);
                if (seen.Add(fid)) outIds.Add(fid);
            }
        }
        return [.. outIds];
    }

    private string[] Materialize(long[] ids)
    {
        var batch = Path.Combine(_dragTmp, (++_batchNo).ToString());
        var paths = new List<string>(ids.Length);

        try { Directory.CreateDirectory(batch); }
        catch (IOException) { return []; }

        using var c = _repo.Open();
        foreach (var id in Expand(c, ids))
        {
            var f = _repo.Get(c, id);
            if (f is null) continue;

            var src = _cfg.ToFull(f.RelPath);

            // Папку жёсткой ссылкой не подменить, отдаём как есть.
            if (Directory.Exists(src)) { paths.Add(src); continue; }
            if (!File.Exists(src)) continue;

            var link = Unique(Path.Combine(batch, Native.SafeName(f.OrigName)));
            try
            {
                Native.Link(link, src);
                paths.Add(link);
            }
            catch (Exception)
            {
                // Другой том или не NTFS: копируем. Не вышло и это: отдаём исходный
                // путь: получатель увидит имя с номером, но файл всё же получит.
                try { File.Copy(src, link, overwrite: true); paths.Add(link); }
                catch (Exception) { paths.Add(src); }
            }
        }

        if (paths.Count == 0) { CleanDir(batch); return []; }

        _keepBatch = batch;
        return [.. paths];
    }

    private void StartDrag(string[] paths)
    {
        if (_dragging) return;
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            // Строго Copy. Move вырвал бы файл из хранилища при переносе в папку,
            // а путь в хранилище задаёт идентичность файла, менять его нельзя.
            DoDragDrop(data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Перетаскивание не удалось: {ex.Message}");
        }
        finally
        {
            _dragging = false;
            // Последнюю пачку не трогаем: получатель мог взять путь и читать позже
            // (браузер отдаёт файл на отправку формы). Её уберёт следующее
            // перетаскивание или закрытие окна.
            CleanDragTmp(_keepBatch);
        }
    }

    /// <summary>Сносит временные ссылки, кроме указанной пачки.</summary>
    private void CleanDragTmp(string? keep)
    {
        if (!Directory.Exists(_dragTmp)) return;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_dragTmp))
            {
                if (keep is not null && string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)) continue;
                CleanDir(d);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CleanDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Приём файлов извне ───────────────────────────────────────────────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // Список путей вытягиваем один раз на вход: DragOver сыплется на каждое
        // движение мыши, и разбирать там весь FileDrop заново было бы впустую.
        _accept = Incoming(e.Data).Length > 0;
        e.Effect = _accept ? DragDropEffects.Copy : DragDropEffects.None;
        if (_accept) Hint(true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = _accept ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        _accept = false;
        Hint(false);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _accept = false;
        Hint(false);
        var paths = Incoming(e.Data);
        if (paths.Length == 0) return;

        // Копирование может тянуться минутами, окно должно остаться живым.
        // Наблюдатель за приёмом подхватит файлы сам, звать сканер незачем.
        Task.Run(() => CopyToInbox(paths));
    }

    /// <summary>Пути, которые есть смысл принимать: файлы со стороны, не из хранилища.</summary>
    private string[] Incoming(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return [];
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return [];

        // Своё же обратно не берём: иначе перетаскивание из окна в окно
        // вернуло бы в приём копию уже разложенного файла.
        return [.. paths.Where(p => !p.StartsWith(_cfg.Root, StringComparison.OrdinalIgnoreCase))];
    }

    private void CopyToInbox(string[] paths)
    {
        Directory.CreateDirectory(_cfg.Inbox);
        foreach (var p in paths)
        {
            try
            {
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
                if (name.Length == 0) continue;   // корень диска перетащили

                var dst = Unique(Path.Combine(_cfg.Inbox, name));
                if (Directory.Exists(p)) CopyDir(p, dst);
                else File.Copy(p, dst);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Не удалось принять {p}: {ex.Message}");
            }
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Оверлей приёма показывает страница, но событие о файлах есть только у формы.</summary>
    private void Hint(bool on)
    {
        var w = _web.CoreWebView2;
        if (w is null) return;
        try
        {
            w.PostWebMessageAsJson(on
                ? """{"kind":"dropHint","on":true}"""
                : """{"kind":"dropHint","on":false}""");
        }
        catch (InvalidOperationException) { /* окно уже закрывается */ }
    }

    // ── Мелочи ───────────────────────────────────────────────────────────

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    private sealed record DragOutMsg(string? Kind, long[] Ids);
}
