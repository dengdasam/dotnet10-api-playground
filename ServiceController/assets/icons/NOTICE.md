# Icon licence

## App icon

Glyph: **plug-zap** from the [Lucide](https://lucide.dev) icon set
(ISC License), obtained via [Iconify](https://iconify.design)
(`https://api.iconify.design/lucide/plug-zap.svg`).

This project only recolours the glyph to white and places it on a solid
rounded square (deep indigo `#3B5A6A` for the app icon, green / grey / red
for the three tray states). Recolouring and composition do not change the
licence. `plug-zap` is not one of the Feather-derived icons listed in
Lucide's LICENSE, so only the ISC terms below apply.

Source SVGs:

- `plug-zap.svg` — the original 24×24 outline, stroke width 2
- `plug-zap-small.svg` — same path with stroke width 3, used at 16–32 px
  where a hairline stroke turns to mush

Regenerate with `python tools/make_icon.py` from the repository root.
It writes `ServiceController/assets/app.ico` (multi-size, used as the exe
file icon) and `ServiceController/assets/glyph.png` (white glyph, used at
runtime to compose the tray state icons).

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Fonts

The UI uses system fonts only (Microsoft YaHei UI / Segoe UI / Consolas);
no font files are shipped or embedded.
