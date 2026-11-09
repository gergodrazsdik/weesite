# seed

The sample site that local development, the tests and the test environment run on. The tests read it directly (`Content:Path`). With `Seed:Path` pointing here, the web app uploads it into Blob Storage at startup (`just aspire`, and test, from the copy in the image): each top-level directory becomes a container, each file a blob named by its path inside it. Only missing files and files changed in `seed/` are written; a file changed in the editor is kept. Prod never loads it.

| Directory | Container | What |
| --- | --- | --- |
| `content/posts/` | `content` | Blog posts: Markdown with YAML front matter |
| `content/pages/` | `content` | Content pages (`kind: page`) |
| `settings/` | `settings` | Site settings (`site.json`) with the placeholder identity; `dateFormat` sets how dates are written for readers (a .NET date format, default `yyyy-MM-dd`). `links` are the site's links in the footer (else the first author's). Admins edit these at `/admin/settings`. Editors' profiles go in `authors.json` beside it, written from `/admin/profile` |
| `media/` | `media` | Images and attachments, named `{hash}-{name}` (the first 12 hex digits of the file's SHA-256), served at `/media/{name}`. Seeding strips the images' metadata and adds WebP variants; `c0f10a37852f-gradient.jpg` carries made-up camera and GPS tags to show that. |

The identity is a placeholder ("Example Author", `example.com`): never put real names or addresses here. Front matter keys: `kind` (`post` or `page`), `title`, `slug`, `date` (posts), `updated`, `author` (an ID in `site.json`), `lang`, `tags`, `summary`, `translation`, `menu` (pages), `redirect_from` (earlier slugs), `image`, `image_alt`, `status` (`published`, the default; `draft`; or `scheduled` with `publish_at`, a time with an offset such as `2030-01-01T08:00:00Z`; ADR 0005). Only published items, and scheduled ones once due, reach the public site: `notes-for-a-future-post` is a draft and `a-post-from-the-future` is scheduled. Raw HTML in the Markdown is shown as text (ADR 0002).

## Photos

The photos in `media/` come from Wikimedia Commons. Each one is public domain or CC0 there, so it needs no attribution; it's credited anyway. They were scaled to 1280 pixels wide and re-encoded without metadata.

| File | Source | Author | Licence |
| --- | --- | --- | --- |
| `854ec192d6e7-audio-plug.jpg` | [Audio-TRS-Mini-Plug.jpg](https://commons.wikimedia.org/wiki/File:Audio-TRS-Mini-Plug.jpg) | Evan-Amos | Public domain |
| `363ad5a444bf-city-night.jpg` | [Price Building illuminated at night in Quebec City.jpg](https://commons.wikimedia.org/wiki/File:Price_Building_illuminated_at_night_in_Quebec_City.jpg) | Wilfredor | CC0 |
| `7e354e2b7180-crete-coast.jpg` | [Komos site baie Crète.jpg](https://commons.wikimedia.org/wiki/File:Komos_site_baie_Cr%C3%A8te.jpg) | Jebulon | CC0 |
| `4c01a5e824a1-espresso.jpg` | [Espresso Coffee 01.jpg](https://commons.wikimedia.org/wiki/File:Espresso_Coffee_01.jpg) | Jubair1985 | CC0 |
| `116c3f9b1f40-keyboard.jpg` | [Keyboard Close Up.jpg](https://commons.wikimedia.org/wiki/File:Keyboard_Close_Up.jpg) | Alexander-design | CC0 |
| `c413646f0110-lake-bridge.jpg` | [General Rafael Urdaneta Bridge view from the lake to Cabimas side.jpg](https://commons.wikimedia.org/wiki/File:General_Rafael_Urdaneta_Bridge_view_from_the_lake_to_Cabimas_side.jpg) | Wilfredor | CC0 |
| `82adc6cea959-lighthouse.jpg` | [Lime Point Lighthouse, SW view.jpg](https://commons.wikimedia.org/wiki/File:Lime_Point_Lighthouse,_SW_view.jpg) | Radomianin | Public domain |
| `e2f2cf3360b3-marigolds.jpg` | [French marigold garden 2009 G1.jpg](https://commons.wikimedia.org/wiki/File:French_marigold_garden_2009_G1.jpg) | George Chernilevsky | Public domain |
| `690a2c4f2bca-monastery-library.jpg` | [2024 Kłodzko, zespół klasztorny franciszkanów, biblioteka (2).jpg](https://commons.wikimedia.org/wiki/File:2024_K%C5%82odzko,_zesp%C3%B3%C5%82_klasztorny_franciszkan%C3%B3w,_biblioteka_(2).jpg) | Jacek Halicki | CC0 |
| `dc52c9498729-railway-bridge.jpg` | [JA1271 crossing the Otaki River.jpg](https://commons.wikimedia.org/wiki/File:JA1271_crossing_the_Otaki_River.jpg) | Karora | Public domain |
| `ecbf632d0a3e-snowy-bike.jpg` | [Bike on a snowy street in Quebec City at night.jpg](https://commons.wikimedia.org/wiki/File:Bike_on_a_snowy_street_in_Quebec_City_at_night.jpg) | Wilfredor | CC0 |
| `5f3313d573ee-snowy-road.jpg` | [Forest road Slavne 2017 G8.jpg](https://commons.wikimedia.org/wiki/File:Forest_road_Slavne_2017_G8.jpg) | George Chernilevsky | Public domain |
| `c0d16c7a20e9-spruce-forest.jpg` | [Spruce forest at Holma.jpg](https://commons.wikimedia.org/wiki/File:Spruce_forest_at_Holma.jpg) | W.carter | CC0 |
| `ee3a9b622ebb-sunset.jpg` | [Juan Griego Sunset.jpg](https://commons.wikimedia.org/wiki/File:Juan_Griego_Sunset.jpg) | Wilfredor | CC0 |
