# Weesite

Weesite is a small, open-source personal-site engine: a blog and a few content pages, served by one ASP.NET Core 11 app (Blazor static server rendering, no frontend framework) on Azure Container Apps for a few dollars a month. It has hybrid search, feeds and first-class support for AI agents, a private editor, and infrastructure in OpenTofu with its own bootstrap. Everything that identifies a site is configuration, so anyone can deploy their own instance.

## Status

**Pre-alpha, design phase.** There is no application or infrastructure code yet. The design is in the [brief](docs/design/brief.md); progress is tracked in the [work tracker](docs/plan/TRACKER.md).

## Planned features

- Blog and content pages from Markdown, with tags, related posts, a one-level menu and a configurable home page
- Hybrid search inside the app: SQLite FTS5 plus local ONNX embeddings, merged with reciprocal rank fusion
- SEO and feeds: structured data, sitemap, RSS/Atom
- AI-agent support: `llms.txt`, Markdown via `Accept: text/markdown`, an MCP endpoint
- A private editor (`/admin`) with Google or Microsoft sign-in, roles, drafts, previews, scheduling and revisions
- Light, dark and system themes from design tokens, with a colour theme editor
- Cookieless server-side analytics that also counts AI agents
- Clean HTML export of posts for publishing elsewhere by hand
- Works without JavaScript; strict CSP, accessible by default; ready for localization
- OpenTofu infrastructure with client-side state encryption, a Rego policy baseline and approval-gated deploys

## Documentation

- [Design brief](docs/design/brief.md): scope, stack and decisions
- [Deploy your own Weesite](docs/deploy.md) (outline; filled in with the bootstrap code)
- [Development setup](docs/dev-setup.md)
- [Contributing](CONTRIBUTING.md), [security policy](SECURITY.md), [support](SUPPORT.md)
- [Architecture decision records](docs/adr/README.md)
- [Work tracker](docs/plan/TRACKER.md)

## Licence

[Apache-2.0](LICENSE), with [DCO](CONTRIBUTING.md#developer-certificate-of-origin-dco) sign-off on every commit. See [NOTICE](NOTICE).
