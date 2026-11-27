# 0012: Faster starts: a native highlighter, and loading in the background

- Status: accepted (maintainer, 2026-11-27)
- Date: 2026-11-27
- Tracker: T-0189, T-0190, T-0191, T-0192
- Amends: [ADR 0002](0002-public-site.md) (highlighting with TextMateSharp), [ADR 0003](0003-search.md) (the first search index waits for the embedding model)

## Context and problem statement

A replica's start decides how long the first visitor to a sleeping test site waits, and whether an MCP client's first connection to it succeeds: the client gives up after about 30 s, and test's first page came after about 35 s. On prod, which keeps a replica warm, it decides how soon a new replica takes load. The app logs every step's time. Over 14 days of starts, in medians:

| Step | test (0.5 vCPU, 96 items, `multilingual-e5-small`) | prod (0.25 vCPU, 7 items, `bge-small-en-v1.5`) |
| --- | --- | --- |
| Host built, Key Vault secrets included | 5.9 s, Key Vault 3.5 s | 3.7–6.0 s, Key Vault 1.0–3.2 s |
| Content load, of which parsing and rendering | 5.8 s, rendering 3.4 s | 4.0–5.3 s, rendering 3.4–4.1 s |
| Embedding model, beside the content | 7.0 s | 1.0–1.6 s |
| App started | 14.7 s | 12.4–19.0 s |

Three findings:

- **Rendering costs the same for 7 items as for 96.** On prod's 160 KB of Markdown, in a fresh ReadyToRun process on one laptop core, Markdig itself takes about 60 ms on first use and 8–12 ms for all the prose. Each TextMate grammar's first use takes the rest: about 550 ms together (bash 220 ms, the first grammar loaded; Markdown 120; JavaScript 100; Just 90; C#, JSON, YAML and PowerShell 2–15 each), and highlighting the 76 blocks once they're loaded another 150 ms. TextMateSharp parses each grammar at run time, so ReadyToRun barely helps. It also adds about 134 MB to the process, on replicas of 0.5 and 1 GiB.
- **Key Vault's time is the managed identity's first token.** Key Vault's audit log shows the client's first request answered with the usual 401 challenge 0.6 s after the process starts, then nothing until the authenticated list: 3.05 s at the median on test (253 starts) and 2.69 s on prod (15 starts). The list and the twelve parallel reads then take about 0.4 s. The CPU is idle meanwhile.
- **The first search index waits for the model.** `LoadingEmbedder` blocks until the model is loaded, and the first build reads its ID, though most embeddings are stored and the model is only needed for queries and new chunks. On test the model takes longer than the content.

## Considered options

| Question | Options considered |
| --- | --- |
| Highlighting's cold cost | Keep TextMateSharp, and only load its grammars earlier; store rendered HTML in Blob Storage, in a bundle per renderer version; Go (chroma) out of process; Rust (syntect) out of process, one process per load or a long-lived sidecar; Rust (syntect) in process, through P/Invoke; all of Markdown in Rust (pulldown-cmark or comrak) |
| Key Vault's wait | Leave it; use the wait for work that needs no Azure access; read the secrets in the background; Container Apps' Key Vault references |
| The model | Leave the first index waiting for it; a keyword-only first index, with embeddings once the model is in |

The highlighting options were measured on prod's Markdown (160 KB, 76 fenced blocks with a language, 66 of them not `text`), with throwaway benchmarks kept out of the repository. Tools built from crates and Go modules were built and run only in sandboxes.

Fresh process each time, one laptop core (i7-11850H), whole file to HTML:

| Tool | Cold, end to end | Warm highlighting pass | Not highlighted |
| --- | --- | --- | --- |
| Markdig + TextMateSharp (today, ReadyToRun) | about 770 ms | about 150 ms | – |
| pulldown-cmark or comrak + syntect 5.3 (Rust) | 37–40 ms | about 18 ms | PowerShell, Just |
| the same, with two-face 0.5.2 (bat 0.26.1's syntaxes) | 42–44 ms | about 18 ms | Just |
| goldmark + chroma 2.27 (Go) | about 90 ms | about 47 ms | Just |

Called from .NET, in prod's runtime image (`aspnet:11.0.0-resolute-chiseled-extra`) at one vCPU, the 66 blocks:

| How | First block | All blocks, cold | Warm pass | Memory added |
| --- | --- | --- | --- | --- |
| TextMateSharp (today) | 246 ms | 611 ms | 70 ms | 134 MB |
| syntect in process (P/Invoke) | 19 ms | 57 ms | 21 ms | 9.5 MB |
| syntect, long-lived sidecar | 48 ms | 86 ms | 22 ms | 9 MB, and 11 MB in the child |
| syntect, one process per load | 94 ms | 108 ms | 57 ms, a new process each time | 11 MB, and 11 MB in the child |
| syntect, one process per block | 62 ms | 1,005 ms | 937 ms | 13 MB |

At four vCPUs the native options were slower, not faster (in process 79 ms, the sidecar 137 ms): each call wakes threads on other vCPUs.

- **Loading TextMateSharp's grammars earlier** hides their time but keeps the 134 MB and the slower warm path.
- **Stored HTML** would save the render only when the renderer hasn't changed: on restarts and new replicas, not on most deploys. It brings a second copy of the content that has to go stale exactly when the HTML would change, and replicas writing it at once. With a native highlighter, it would save about 0.3 s.
- **Go** is 8–9 times cheaper than today, but chroma's Pygments-style token classes don't map onto Weesite's TextMate scope classes, and it runs out of process.
- **Rust out of process** works, but pays a spawn per load or a process to supervise.
- **Markdown in Rust** gains 7–10 ms over Markdig, and Weesite's own Markdown rules (figures, image placement, alt-text checks, `srcset`, the editor's `data-line`) are Markdig extensions.
- **Key Vault references** in Container Apps would cost the app nothing, but every secret must be declared in `infra/app`, and a missing optional one, such as a sign-in provider's, fails the revision.

## Decision outcome

The maintainer chose (2026-11-27):

| Question | Decision |
| --- | --- |
| Highlighting | syntect in a Rust library that the app calls through P/Invoke; Markdig stays |
| Grammars | bat's syntaxes (through two-face) as the base, with what the site highlights today kept |
| Key Vault's wait | Used: work that needs no Azure access starts at the first line of `Program` |
| The model | The first index is keyword-only when the model isn't loaded yet; the embeddings follow |
| Secrets in the background | Done once the first tokens' times were logged (T-0192): see the [ADR 0004 amendment](0004-sign-in.md#amendment-2026-11-27-the-secrets-load-in-the-background-t-0192) |
| Stored renders | Not taken |

### The highlighter

- A Rust crate builds a library (`.so` on Linux) with a C interface: highlight a block in a language into HTML, free the result. syntect's `SyntaxSet` is loaded once per process from a dump embedded in the library, on the first call. syntect compiles each pattern on first use and keeps it, so after a language's first block every later one is warm. The set is safe to share between threads, so the lock around TextMateSharp goes.
- Output keeps Weesite's classes: the library hands back the scopes, or classes from them, that Weesite maps to its `hl-*` classes as today, so the CSS and the output's size stay as they are.
- The limits stay: lines over 1,000 characters and blocks over 20,000 stay plain, and the document's highlighting budget is checked between lines. syntect can't stop within a line, as TextMateSharp's line time limit did, so Oniguruma's retry limit bounds how long one pattern may backtrack.
- Highlighted blocks are still kept in Weesite's own cache, and a block's HTML depends only on its code and language, as now.
- A panic in the library is caught at its boundary and never crosses into .NET. If the library can't load, code renders plain and the log says so once.
- The library is built in the image (a Rust stage in the `Containerfile`, its image pinned by digest) and in CI, with a pinned toolchain and a committed `Cargo.lock`. Dependabot watches cargo, and the tool updates watch the Rust toolchain and image. Local builds need Rust ([dev-setup.md](../dev-setup.md)).

### Grammars

- The base is bat's set of Sublime syntaxes as two-face embeds it: about 190 languages, maintained upstream in their native format, built with Oniguruma.
- Every language the site highlights today keeps working. One bat lacks gets a `.sublime-syntax` file: from an existing Sublime package where there is one (Just), otherwise converted from its VS Code grammar (Bicep, plain HCL), as bat's documentation describes. syntect reads only Sublime's format. In the current set, 93 of 142 grammar files use neither `while` rules nor `\G` anchors, which a conversion has to handle by hand; the migration lists any language it can't convert, for the maintainer to decide.
- Weesite's own fence names (`cs`, `c#`, `yml`, `console`, `shell`, `pwsh`) and the language list in the editor stay. [grammars.md](../grammars.md) lists the new set and its sources.
- The added grammars live in the crate, pinned with their licences, as `src/Weesite.Content/Grammars/` does now.

### Startup

- The first line of `Program` starts what needs no Azure access: the embedding model's load and a warm-up of the highlighter. It also asks for the storage token, so the content's first read doesn't wait for one of its own. Key Vault's secrets load meanwhile, as now.
- The app logs each scope's first token time, so the next step (T-0192) is decided on measurements.
- When the model isn't loaded yet, the first index is built from keywords alone, and the replica reports ready. Searches are keyword-only until the model is in, then a build adds the embeddings, stored ones first. When the model fails to load, search stays keyword-only, as today.

### Consequences

- The repository gains a second language and its toolchain: Rust for the highlighter, with its own dependencies, updates and supply-chain checks.
- Highlighted code looks slightly different: bat's grammars split some tokens differently from VS Code's. The screenshots show it.
- Each replica uses about 125 MB less memory, and starts an estimated 5–7 s sooner: on prod, from 12–15 s to about 7 s.
- For a few seconds after a replica starts, its search finds by keywords only.
- Key Vault's token wait (about 3 s) was on the critical path until T-0192; since then only sign-in, the admin and mail wait for it.

## More information

- [syntect](https://github.com/trishume/syntect), [two-face](https://github.com/CosmicHorrorDev/two-face), [bat's syntaxes and their conversion](https://github.com/sharkdp/bat/blob/master/doc/assets.md)
- [Oniguruma](https://github.com/kkos/oniguruma)
- [P/Invoke source generation (`LibraryImport`)](https://learn.microsoft.com/dotnet/standard/native-interop/pinvoke-source-generation)
