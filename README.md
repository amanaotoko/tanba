# Tanba

A tag-based file catalog for Windows, built to replace folders.

## Why

A folder tree gives every file exactly one parent, but a real file belongs to
several categories at once. A promo video is both "video" and "career
guidance", and it belongs to two of the five institutions in our education
complex. A tree forces you to pick one truth and throw the rest away, and then
you copy the file three times so it can be found from three places.

Tanba keeps files flat on disk and puts all organisation in a database. A file
is stored once and can be found from as many directions as you like. Nothing is
ever moved in order to be classified.

## How the work goes

**Save anything into `СОХРАНИ СЮДА`.** That is the only decision you make while
working: no subfolders, no naming rules, no thinking about where this belongs.
The folder sits at the root of the storage drive and is the only one you ever
open by hand.

**Sort the pile later, in one go.** The Triage screen shows everything waiting.
Select a few files, tick the tags that apply to all of them, press Разложить.
The files move into storage and disappear from the pile. Tagging fifty files
takes about as long as tagging five, because you tag them in batches rather
than one at a time.

**Find things on the Library screen.** Pick tags and the list narrows. Combine
them with И (all of them) or ИЛИ (any of them), add a file format, a date
range, or a piece of the file name. The query is a set of conditions, not a
path, so the order you pick things in does not matter.

**Group work into catalogs.** A catalog looks like a folder and behaves like
one when you walk into it, but it is a relation, not a place. The same file can
sit in several catalogs at once, catalogs can nest, and deleting a catalog only
breaks the link. The file itself never moves and never disappears.

## What it does

**Previews for the formats designers actually use.** Tanba asks Windows for the
thumbnail, so `.cdr`, `.ai`, `.psd` and anything else render through the preview
handlers CorelDRAW and Adobe already installed. No format support to wait for:
coverage grows with whatever is on the machine.

**Duplicates merge instead of piling up.** Two identical files keep one copy and
combine their tags, so the second copy adds information rather than waste.

**Files drag out into anything.** Pull a file from the window straight into a
browser upload form, a chat, or a folder, exactly as you would from Explorer.
It always copies, never moves: the file stays in the catalog.

**CorelDRAW backups become versions.** `Резервная копия_X.cdr` is recognised as
the previous state of `X.cdr` instead of being left to litter the folder.

**Deleting goes to the recycle bin.** Restore a file from there and it comes
back with its tags intact.

**Updates happen when you press the button**, never in the middle of your work.
Tanba can also start with Windows and sit in the notification area, counting
what is waiting to be sorted.

## Installing

Download `Tanba-win-Setup.exe` from the
[Releases](https://github.com/amanaotoko/tanba/releases) page and run it.

It installs for the current user only and needs no administrator rights.
Because the installer is not code-signed, Windows shows a "Windows protected
your PC" warning on first run: choose **More info**, then **Run anyway**.

By default Tanba expects its storage on drive `S:`. To keep it somewhere else,
set an environment variable before starting:

```
setx TANBA_ROOT D:\archive
```

On first run the program creates the folders it needs and an empty database.

## Updating

Open Настройки and press Проверить. If a newer version is out, the button
offers to install it, and Tanba restarts on the new version. Everything on the
storage drive is untouched by updates.

## What lands on the disk

```
СОХРАНИ СЮДА    the only folder you open yourself
_store          the files, filed by year and month
_versions       previous states of edited files
_thumbs         cached previews
_meta           the database, a daily backup, and catalog.csv
```

`catalog.csv` is a flat export of "where each file lies and how it is tagged".
It is rewritten after every sort, opens in Excel, and exists so that the
contents of the catalog stay readable even if the program itself is gone.

## Status

Built for one design team and used daily. Interface and documentation for the
people using it are in Russian; the code and this page are in English.
See [DEVELOPING.md](DEVELOPING.md) if you want to build or change it.

## Licence

Tanba is free software under the **GNU General Public License, version 3**. The
full text is in [LICENSE](LICENSE). You may use, study, change and pass it on;
if you distribute a changed version, you have to pass the source on with it.

> This program is distributed in the hope that it will be useful, but WITHOUT
> ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
> FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

The choice was made by the file-type icons rather than by preference. They come
from the [Suru++](https://github.com/suru-plus/suru-plus) theme, which is
GPL-3, and it is close to the only icon set in existence that draws CorelDRAW
rather than falling back to a blank sheet. In a catalog that is two thirds
`.cdr`, that decides it. See [ui/icons/NOTICE.md](ui/icons/NOTICE.md) for what
else was considered and why each alternative failed.
