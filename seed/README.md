# seed

The sample site that local development, the tests and the test environment run on. The tests read it directly (`Content:Path`). With `Seed:Path` pointing here, the web app uploads it into Blob Storage at startup (`just aspire`, and test, from the copy in the image): each top-level directory becomes a container, each file a blob named by its path inside it. Prod never loads it.

| Directory | Container | What |
| --- | --- | --- |
| `content/posts/` | `content` | Blog posts: Markdown with YAML front matter |
| `content/pages/` | `content` | Content pages (`kind: page`) |
| `settings/` | `settings` | Site settings (`site.json`) with the placeholder identity |
| `media/` | `media` | Images and attachments, named `{hash}-{name}` (the first 12 hex digits of the file's SHA-256), served at `/media/{name}` |

The identity is a placeholder ("Example Author", `example.com`): never put real names or addresses here. Front matter keys: `kind` (`post` or `page`), `title`, `slug`, `date` (posts), `updated`, `author` (an ID in `site.json`), `lang`, `tags`, `summary`, `translation`, `menu` (pages), `redirect_from` (earlier slugs), `image`, `image_alt`. Raw HTML in the Markdown is shown as text (ADR 0002).
