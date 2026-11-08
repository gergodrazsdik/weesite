# Fonts

The fonts `OpenGraphCard` draws the generated Open Graph cards with. They're copied next to the app, so cards look the same everywhere, the chiseled container image included (it has no system fonts).

| File | Font | Source | Licence |
| --- | --- | --- | --- |
| `Inter-Bold.ttf` | Inter Bold (titles) | [rsms/inter](https://github.com/rsms/inter) release [v4.1](https://github.com/rsms/inter/releases/tag/v4.1), `Inter-4.1.zip`, `extras/ttf/` | SIL Open Font License 1.1 (`LICENSE.txt`) |
| `Inter-Regular.ttf` | Inter Regular (site name) | as above | as above |

`Inter-4.1.zip` has SHA-256 `9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e`.

`fonts.conf` is a minimal Fontconfig configuration: this folder, and a cache in the app user's `~/.cache/fontconfig` (the image's `/tmp` isn't writable). The container image sets `FONTCONFIG_FILE` to it, because the chiseled image has no Fontconfig configuration of its own; without it, libvips still finds the fonts (they're passed by file) but Fontconfig prints errors.

To change the font, replace the files and the names in `OpenGraphCard`, update this table and `NOTICE`, and look at a generated card (`/og/…png` in `just aspire`, or the card tests).
