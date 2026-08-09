# Developing Tanba

Everything needed to build, change and release it. For what the program is and
how it is used, see [README.md](README.md).

## Stack

.NET 9 on Windows, SQLite with FTS5, an ASP.NET Core minimal API bound to
`127.0.0.1:5577`, a WinForms host carrying WebView2, and plain HTML and CSS for
the interface. No framework, no build step for the front end: the files in `ui`
are served as they are.

## Running from source

```
dotnet run --project src/Tanba
```

Opens its own window. Add `--no-window` to run headless and use a browser at
`http://127.0.0.1:5577`, though dragging files out will not work there, and
neither will the title bar. `--tray` starts into the notification area without
opening the window; that is what the autostart entry uses.

The storage root defaults to `S:\` and is overridden with `TANBA_ROOT`. On
first run the program creates its folders and applies `schema.sql`.

A running copy holds the executable open, so stop it before building.

## Layout

```
schema.sql              database, embedded into the binary
build.ps1               publish and pack a release
src/Tanba/
  Config.cs             disk layout
  Storage/              database, models, data access, catalog.csv export
  Scanner/              intake, hashing, watcher, filing
  Shell/                P/Invoke: thumbnails, hard links, recycle bin
  Update/               self-update, start with Windows
  Web/                  library, catalog, tag and settings endpoints
  Host/                 WebView2 window, custom title bar, tray icon, native drag
ui/                     interface, one HTML file per screen plus shared chrome.js
```

## The parts worth knowing before changing them

**Thumbnails come from the shell.** `IShellItemImageFactory` asks Windows for a
preview of any path, which is why closed formats render at all. There is no
format parsing on our side and there should not be.

**Dragging files out needs the host.** A drop into a browser upload form or a
folder requires a real `CF_HDROP`, which HTML5 drag cannot produce. The WinForms
host runs `DoDragDrop` over hard links carrying clean names, because a file is
stored as `000147__name.ext` and has to leave as `name.ext`. Always
`DragDropEffects.Copy`: the storage path is the file's identity and moving it
would break every tag pointing at it.

**The window owns its whole frame.** `WM_NCCALCSIZE` answers zero, so there is
no system caption and no border left showing. That means the edges are covered
by web content and Windows cannot reach them, so resizing is driven from the
page: it tracks the pointer and reports an offset, and the host moves the
bounds. The documented route, `WM_NCLBUTTONDOWN`, does not work here because
the mouse is captured by the WebView2 process and the system resize loop never
sees the movement or the button release.

**Autostart points at the root stub.** The registry entry under
`HKCU\...\CurrentVersion\Run` targets `%LocalAppData%\Tanba\Tanba.exe`, not the
copy inside `current`: that whole directory is replaced while an update
applies, and a logon can race it. The stub is re-extracted after every update,
so it heals itself. Enabled state is read back from the registry including the
undocumented `StartupApproved` mark, so turning Tanba off in Windows settings
shows up in ours.

**The first inbox scan runs in the background.** It used to sit in the watcher's
constructor and hold startup until every file had been hashed, which is half a
minute on a hundred files and would be half a minute of every boot once
autostart is on.

**Static files are served with `no-store`.** WebView2 keeps its own cache
between runs, and a stylesheet edit would otherwise show yesterday's layout in
the window while looking correct in a browser.

## Releasing

```
.\build.ps1 0.2.0
```

Produces an installer and an update package in `build\releases` via
[Velopack](https://velopack.io). Keep previous releases in that folder: deltas
are computed against them, which is the difference between a 0.2 MB update and
a 63 MB one.

Pushing a version tag runs the same steps in CI and publishes a GitHub release:

```
git tag v0.2.0 && git push origin main --tags
```

The version in `Tanba.csproj` is only a fallback for debug builds; the real one
comes from `/p:Version` and from the tag.

Installed copies live in `%LocalAppData%\Tanba`, per user and without admin
rights. Storage on the S: drive is untouched by updates. The binaries are
unsigned, so SmartScreen warns until someone clicks through; a certificate is
not worth buying for an internal tool, and a self-signed one installed on the
machines would remove the warning if it ever becomes a nuisance.

The repository is public, so the app reads its releases without any credential
and ships no secret. `TANBA_UPDATE_TOKEN` still exists in the build for the day
the repository might be closed again; note that a baked-in token can be
extracted from the binary, so it would have to be read-only and scoped to this
repository alone.

## Conventions

Interface strings and source comments are in Russian, because the people using
the program are. Repository-level things stay in English: this file, the README,
commit messages.

Comments explain why, not what. A comment that restates the line above it is
noise; a comment that records which approach failed and why saves the next
person a day.

Design work lives outside the repository. Mockups and briefs are kept on disk
and ignored by git, partly because they never reach the executable and partly
because some reference images are other people's work.
