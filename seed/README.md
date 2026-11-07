# seed

The sample site that local development and the end-to-end tests run on. `aspire run` (`just aspire`) uploads it into Azurite with `Weesite.Seed`: each top-level directory becomes a Blob container, each file a blob named by its path inside it.

| Directory | Container | What |
| --- | --- | --- |
| `content/posts/` | `content` | Blog posts: Markdown with YAML front matter |
| `content/pages/` | `content` | Content pages (`kind: page`) |
| `settings/` | `settings` | Site settings (`site.json`) with the placeholder identity |

The identity is a placeholder ("Example Author", `example.com`): never put real names or addresses here. The front matter and settings formats are a draft until the content model (T-0032) settles them.
