namespace Tanba.Scanner;

/// <summary>
/// Следит за папкой приёма. События файловой системы сыплются пачками,
/// поэтому реальное сканирование запускается один раз после затишья.
/// </summary>
public sealed class InboxWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly System.Threading.Timer _debounce;
    private readonly Ingest _ingest;
    private int _running;

    /// <summary>Сколько файлов ждёт разбора после очередного пересчёта.</summary>
    public event Action<int>? Changed;

    public int Pending { get; private set; }

    public InboxWatcher(Config cfg, Ingest ingest)
    {
        _ingest = ingest;
        Directory.CreateDirectory(cfg.Inbox);

        _debounce = new System.Threading.Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);

        _fsw = new FileSystemWatcher(cfg.Inbox)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Created += (_, _) => Bump();
        _fsw.Changed += (_, _) => Bump();
        _fsw.Deleted += (_, _) => Bump();
        _fsw.Renamed += (_, _) => Bump();
        _fsw.Error += (_, _) => Bump();
        _fsw.EnableRaisingEvents = true;

        Rescan();
    }

    /// <summary>Копирование крупного файла тянется, поэтому ждём полторы секунды тишины.</summary>
    private void Bump() => _debounce.Change(1500, Timeout.Infinite);

    public void Rescan()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) { Bump(); return; }
        try
        {
            Pending = _ingest.ScanInbox();
            Changed?.Invoke(Pending);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Сканирование приёма упало: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose()
    {
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
