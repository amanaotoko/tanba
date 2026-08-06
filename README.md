# Tanba

A tag-based file catalog for a design team, built to replace folders.

The problem it solves: a folder tree gives every file exactly one parent, but a
real file belongs to several categories at once. A promo video is both "video"
and "career guidance", and it belongs to two of the five institutions in our
education complex. A tree forces you to pick one truth and throw the rest away.

Tanba keeps files flat on disk and puts all organisation in a database. Nothing
is ever moved to be classified.

## How it works

Files land in one folder called `СОХРАНИ СЮДА` ("save here"). That is the only
decision a person makes: no subfolders, no naming rules. A watcher picks them
up, and you tag them later in batches.

Filing moves a file into `_store\YYYY\MM\NNNNNN__original name.ext` and never
touches it again. The path is the file's identity: editing a file changes its
hash, not its place. Everything else, tags included, lives in SQLite.

**Catalogs** are relations that look like folders. A file can sit in several
catalogs at once, catalogs can nest, and deleting a catalog only breaks the
link. Nothing leaves the disk.

## Notable pieces

**Thumbnails come from the Windows shell.** `IShellItemImageFactory` asks the OS
for a preview of any file, so `.cdr`, `.ai` and `.psd` render through the
handlers CorelDRAW and Adobe already registered. No format parsing on our side,
and coverage grows with whatever the user installs.

**Duplicates merge instead of piling up.** Two identical files keep one copy and
combine their tags, so the copy contributes information instead of waste.

**Drag out is native.** Dropping a file into a browser upload form or a folder
needs a real `CF_HDROP`, which HTML5 drag cannot produce. A WinForms host runs
`DoDragDrop` with hard links carrying clean names. Copy only, never move: the
storage path is the file's identity.

**Corel backup files become versions.** `Резервная копия_X.cdr` is the previous
state of `X.cdr`. Instead of ignoring the litter, the scanner recognises it.

## Stack

.NET 9 on Windows, SQLite with FTS5, ASP.NET Core minimal API on 127.0.0.1,
WebView2 host, plain HTML and CSS for the interface.

## Running it

```
dotnet run --project src/Tanba
```

Opens its own window. Add `--no-window` to run headless and use a browser at
`http://127.0.0.1:5577`, though drag out will not work there.

The storage root defaults to `S:\` and can be overridden:

```
set TANBA_ROOT=D:\archive
```

On first run the program creates its folders and applies `schema.sql`.

## Layout

```
schema.sql              database, embedded into the binary
src/Tanba/
  Config.cs             disk layout
  Storage/              database, models, data access, catalog.csv export
  Scanner/              intake, hashing, watcher, filing
  Shell/                P/Invoke: thumbnails, hard links
  Web/                  library and catalog endpoints
  Host/                 WebView2 window, tray icon, native drag
ui/                     interface
claude-design/          design system, decisions, house rules
```

## A note on language

The product is Russian: interface strings, source comments and design documents
are written in Russian because the people using it are. Repository-level things
stay in English.
