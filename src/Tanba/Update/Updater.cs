using System.Reflection;
using Tanba.Storage;
using Velopack;
using Velopack.Sources;

namespace Tanba.Update;

/// <summary>Что показать на экране настроек про версию и обновление.</summary>
public sealed record UpdateState(
    string Version,
    bool Installed,
    string Source,
    bool Configured,
    string? Available,
    bool PendingRestart,
    long? CheckedAt,
    bool Busy,
    string? Error);

/// <summary>
/// Обновление по кнопке, а не втихую. Тихое обновление посреди работы
/// закрыло бы окно в тот момент, когда человек размечает пачку файлов,
/// поэтому проверка и установка делаются только руками из настроек.
/// </summary>
public sealed class Updater(Prefs prefs)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _found;
    private string? _error;
    private volatile bool _busy;

    /// <summary>
    /// То, что вшито при сборке: адрес репозитория и ключ доступа к нему.
    /// Лежит в атрибутах сборки, потому что тогда человеку нечего вписывать,
    /// а ключ не приходится хранить в базе на диске.
    /// </summary>
    private static string? Built(string key) =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    public static string? BuiltRepo => Built("UpdateRepo");
    private static string? BuiltToken => Built("UpdateToken");

    /// <summary>Версия сборки. Единственный источник, из которого её видно и в окне, и в логе.</summary>
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public UpdateState State()
    {
        var (source, configured) = Describe();
        var um = TryManager();

        return new UpdateState(
            Version: um?.CurrentVersion?.ToString() ?? Version,
            Installed: um?.IsInstalled ?? false,
            Source: source,
            Configured: configured,
            Available: _found?.TargetFullRelease.Version.ToString(),
            PendingRestart: um?.UpdatePendingRestart is not null,
            CheckedAt: long.TryParse(prefs.Get(Prefs.UpdateSeen), out var t) ? t : null,
            Busy: _busy,
            Error: _error);
    }

    /// <summary>Спросить источник, есть ли версия новее. Ничего не качает.</summary>
    public async Task<UpdateState> CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return State();
        _busy = true;
        try
        {
            _error = null;
            var um = TryManager();

            if (um is null) { _error = "Источник обновлений не настроен"; return State(); }
            if (!um.IsInstalled)
            {
                // Отладочная сборка живёт в bin и обновлять себя не умеет:
                // ей неоткуда взять ни версию пакета, ни папку установки.
                _error = "Программа запущена не из установленной копии, обновлять нечего";
                return State();
            }

            _found = await um.CheckForUpdatesAsync();
            prefs.Set(Prefs.UpdateSeen, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        catch (Exception ex)
        {
            _found = null;
            _error = Explain(ex);
        }
        finally { _busy = false; _gate.Release(); }

        return State();
    }

    /// <summary>
    /// Скачать найденное и перезапуститься на нём. Возврата из этого метода
    /// в нормальном случае нет: процесс заменяется новым.
    /// </summary>
    public async Task<UpdateState> ApplyAsync(string[] restartArgs)
    {
        if (!await _gate.WaitAsync(0)) return State();
        _busy = true;
        try
        {
            _error = null;
            var um = TryManager();
            if (um is null || _found is null) { _error = "Обновление не найдено"; return State(); }

            await um.DownloadUpdatesAsync(_found);
            um.ApplyUpdatesAndRestart(_found.TargetFullRelease, restartArgs);
        }
        catch (Exception ex)
        {
            _error = Explain(ex);
        }
        finally { _busy = false; _gate.Release(); }

        return State();
    }

    // ── Источник ─────────────────────────────────────────────────────────

    private UpdateManager? TryManager()
    {
        try
        {
            var src = BuildSource();
            return src is null ? null : new UpdateManager(src, new UpdateOptions
            {
                AllowVersionDowngrade = false,
            });
        }
        catch (Exception ex)
        {
            _error ??= Explain(ex);
            return null;
        }
    }

    /// <summary>
    /// Источник один: репозиторий, вшитый при сборке. Выбор между ним и
    /// папкой в локальной сети из настроек убран, потому что выбирать было
    /// не из чего: папку никто не заполнял, а лишний переключатель на экране
    /// заставляет думать о том, о чём думать не надо.
    /// </summary>
    private IUpdateSource? BuildSource()
    {
        var repo = BuiltRepo;
        if (string.IsNullOrWhiteSpace(repo)) return null;

        // Ключ нужен только приватному репозиторию. Публичный читается без него,
        // и тогда в программе не лежит вообще никакого секрета.
        return new GithubSource(repo, BuiltToken, prerelease: false);
    }

    private (string Text, bool Configured) Describe() =>
        string.IsNullOrWhiteSpace(BuiltRepo)
            ? ("репозиторий не задан при сборке", false)
            : (BuiltRepo!, true);

    /// <summary>Ошибки сети и доступа человеку надо объяснить, а не показывать стек.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "Репозиторий или релиз не найден. Если репозиторий приватный, нужен токен",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } or
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "Доступ закрыт. Проверь токен",
        HttpRequestException => "Не удалось связаться с источником обновлений",
        DirectoryNotFoundException => "Папка с обновлениями недоступна",
        // Про пустой репозиторий Velopack сообщает обычным исключением
        // с английским текстом, и он выходил в русское окно как есть.
        _ when ex.Message.StartsWith("No releases found") =>
            "В репозитории ещё нет ни одного релиза",
        _ => "Проверка не удалась: " + ex.Message,
    };
}
