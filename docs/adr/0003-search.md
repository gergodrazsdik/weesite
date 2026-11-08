# 0003: Search choices for Phase 3

- Status: accepted
- Date: 2026-11-08
- Tracker: T-0040 to T-0045

## Context and problem statement

The [brief](../design/brief.md) designs hybrid search in detail (SQLite FTS5 in memory, a local ONNX embedding model, RRF), but leaves open how the model reaches the image, where embeddings come from before the editor publishes anything, when the re-index job is built, and where visitors search.

## Decision outcome

| Topic | Decision | Why |
| --- | --- | --- |
| Code layout | A `Weesite.Search` project next to `Weesite.Content` | keeps SQLite and ONNX Runtime out of the content library |
| Model file | Downloaded at image build (and by a `just` recipe for local runs) from Hugging Face, pinned to a commit and checked by SHA-256; not committed | the repo stays small; the pin and the hash make the build reproducible |
| Embeddings before the editor | Computed in memory when content loads; Blob vector files and per-post embedding at publish come with the editor | test has a few seed posts and prod is empty, so startup cost is small |
| Re-index job (T-0045) | Moves to Phase 4, built with the editor's "re-embed all" action | it has no other trigger; Phase 3 needs no infrastructure change |
| Search UI | A search form in every page's header (`GET /search?q=`) and a results page with snippets, labelled post or page | findable everywhere, and works without JavaScript |

### Consequences

- Image builds and CI need huggingface.co reachable; a changed or missing file fails the build on its hash.
- Startup embeds every chunk until publishing exists; when content grows, this moves to Blob as the brief describes.
- The search page is `noindex`: result pages are for visitors, not search engines.
