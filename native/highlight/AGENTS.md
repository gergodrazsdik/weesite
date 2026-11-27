# Agent instructions: native/highlight/

The code highlighter: a Rust library (syntect with bat's syntaxes, through two-face) that the app calls through P/Invoke (ADR 0012). The rules in the root [AGENTS.md](../../AGENTS.md) apply; these add to them.

- `just highlighter` checks it: rustfmt, clippy, the Rust tests, and that the grammars and [grammars.md](../../docs/grammars.md)'s list agree. `dotnet build` builds the library with cargo; `-p:WeesiteSkipHighlighter=true` skips that, and the app then shows code plain.
- `build.rs` dumps the syntax set at build time. Weesite's own grammars are added to bat's set, not swapped in; converting a TextMate grammar goes through `scripts/convert-grammars.py`, and adding one follows [grammars.md](../../docs/grammars.md).
- Scopes become the `hl-*` classes through `SCOPE_CLASSES` and `CONTEXT_CLASSES` in `src/lib.rs`, the most specific scope first; the fence names are in `src/languages.rs`, and a language's first name is what the editor's Code block dialog inserts.
- The syntax set loads once per process (`LazyLock`); the app warms it up at start, beside the token wait.
- Toolchain: `rust-toolchain.toml` names `stable`. Rustup can't install or update a toolchain inside the Claude sandbox: that's the maintainer's machine. Cargo builds and tests work there, crates.io included.
- New crates: check their licences and release dates (7 days old, as every dependency here) and keep `Cargo.lock` committed; Dependabot proposes updates. Licences of the syntaxes are in `SYNTAX-LICENSES.md`.
