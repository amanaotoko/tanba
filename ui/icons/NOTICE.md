# Where these icons come from

The seventeen SVG files in this directory are **not ours**. They are taken from
the **Suru++** icon theme and are covered by its licence, not by Tanba's.

- Upstream: https://github.com/suru-plus/suru-plus
- Source path: `Suru++/mimetypes/scalable/`
- Licence: **GPL-3.0-only**, with upstream notices in the project's `COPYING`:
  the original Suru set by Sam Hewitt under LGPL-3, and Papirus by
  Alexey Varfolomeev under GPL-3.

Because these files are here, the application that ships them is a GPL-3
distribution. That is a deliberate choice, not an oversight. See the `LICENSE`
file at the root of the repository.

## Why this set and not another

CorelDRAW decides it. A designer's catalog here is roughly two thirds `.cdr`,
and almost no icon set draws it:

| set | licence | `.cdr` |
| --- | --- | --- |
| vscode-icons | MIT + CC BY-SA | absent |
| Seti UI | MIT | absent |
| Tabler, Phosphor, Bootstrap, Iconoir | MIT | absent |
| Fluent UI System Icons | MIT | absent |
| Material Icon Theme | MIT | present, but `cdr` sits in the **audio** extension list and resolves to a music icon |
| Numix, Zafiro, Fluent icon theme | GPL-3 | filename exists, but it is a symlink to the generic vector icon |
| Office / SharePoint assets | proprietary, not redistributable | absent |
| **Suru++** | **GPL-3** | **real artwork: the CorelDRAW balloon** |
| Gruvbox Plus | GPL-3 | real artwork, derived from Suru++, heavier files |

Every one of those was checked against an actual file listing rather than from
memory. The permissive sets are permissive and useless here; the sets that know
CorelDRAW are all copyleft. That is the trade this repository accepted.

## What was changed

Each file had one thing removed: a drop shadow, drawn as a black shape at 0.4
opacity behind the page under a `feGaussianBlur` filter. It is invisible against
a dark background and the browser must still render it on every tile. Removing
it took the set from 80 KB to 69 KB. Nothing else was touched, and the file
names were shortened to the extension they serve.

Regenerating them is a matter of fetching the upstream files listed in
`clean.py`'s map and stripping the same two elements. Note that 77% of the
upstream directory is symlinks: `image-x-psd.svg` is a 24-byte text file whose
content is the name of the real icon, so a plain HTTP fetch of a name may hand
you a string instead of a picture.
