using Tanba.Storage;
using Tanba.Update;

namespace Tanba.Web;

/// <summary>
/// Экран «Настройки»: версия, обновление по кнопке, автозапуск, хранилище.
/// </summary>
public static class SettingsApi
{
    public static void MapSettings(this WebApplication app, Config cfg, Prefs prefs, Updater updater,
        Func<int> pending, Func<bool> scanning)
    {
        app.MapGet("/api/settings", () => Results.Ok(Snapshot(cfg, prefs, updater, pending, scanning)));

        // ── Обновление ───────────────────────────────────────────────────

        app.MapPost("/api/update/check", async () =>
        {
            await updater.CheckAsync();
            return Results.Ok(Snapshot(cfg, prefs, updater, pending, scanning));
        });

        // Установка убивает процесс, поэтому ответ на этот запрос должен уйти
        // раньше неё. Иначе окно увидит оборванное соединение вместо «пошло».
        //
        // Новая копия поднимается с окном, а не в трей. Обновление затевает
        // человек, который сейчас смотрит в экран настроек, и ему обещано, что
        // окно откроется заново; с ключом --tray оно не открывалось, и
        // программа молча уходила значком в угол.
        app.MapPost("/api/update/apply", () =>
        {
            _ = Task.Run(() => updater.ApplyAsync([]));
            return Results.Ok(new { started = true });
        });

        // ── Язык ─────────────────────────────────────────────────────────
        // Окно держит быструю копию выбора в localStorage, чтобы не мигать
        // русским при открытии. Здесь настоящее место хранения: его читает
        // значок в трее, который никакого localStorage не видит.

        // Дешёвый вопрос «на каком языке мы сейчас». Его задаёт каждая страница
        // при загрузке: она рисует себя по быстрой копии из localStorage, а
        // потом сверяется с настоящим значением. Иначе выбор, сделанный в
        // мастере, до главного окна мог не доехать: у мастера своё окно и свой
        // профиль WebView2, а значит и свой localStorage.
        app.MapGet("/api/lang", () => Results.Ok(new { lang = Lang.Pick(prefs.Get(Prefs.Lang)) }));

        app.MapPost("/api/lang", (LangPatch p) =>
        {
            var lang = Lang.Pick(p.Lang);
            prefs.Set(Prefs.Lang, lang);
            Words.Use(lang);
            return Results.Ok(new { lang });
        });

        // ── Автозапуск ───────────────────────────────────────────────────
        // Отвечаем тем, что получилось на самом деле: запись мог не дать
        // сделать администратор, и молча показывать «включено» нельзя.

        app.MapPost("/api/startup", (StartupPatch p) => Results.Ok(new { on = Startup.Set(p.On) }));

        // ── Хранилище ────────────────────────────────────────────────────
        // Путь берём из настроек, а не из запроса: иначе страница получила бы
        // возможность запустить что угодно чем угодно.

        app.MapPost("/api/openroot", () =>
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(cfg.Root) { UseShellExecute = true });
            return Results.Ok(new { ok = true });
        });
    }

    private static object Snapshot(Config cfg, Prefs prefs, Updater updater,
        Func<int> pending, Func<bool> scanning)
    {
        var u = updater.State();
        return new
        {
            version = u.Version,
            // Настоящий выбор языка. У окна есть быстрая копия в localStorage,
            // и разойтись они могут только если базу подменили под ним.
            lang = Lang.Pick(prefs.Get(Prefs.Lang)),
            root = cfg.Root,
            inbox = cfg.Inbox,
            pending = pending(),
            scanning = scanning(),
            startup = Startup.IsOn(),
            startupTarget = Startup.TargetExe(),
            update = new
            {
                installed = u.Installed,
                repo = Updater.BuiltRepo ?? "",
                where = u.Source,
                configured = u.Configured,
                available = u.Available,
                pendingRestart = u.PendingRestart,
                checkedAt = u.CheckedAt,
                busy = u.Busy,
                error = u.Error,
            },
        };
    }

    private sealed record StartupPatch(bool On);
    private sealed record LangPatch(string? Lang);
}
