@AGENTS.md

## Claude Code

Additions for Claude Code only; everything shared is in AGENTS.md (imported above).

- **Sandbox:** `.claude/settings.json` turns on the Bash sandbox with a network allowlist (GitHub, NuGet, .NET, OpenTofu registry, Microsoft container registry) and denies reading secrets, state and `*.auto.tfvars`. Some things can't run inside it and run as separate calls outside, as your user-level setup allows:
  - commit signing and `gh`: commit with `git commit -s -F <file>` and open PRs with `gh pr create --body-file <file>`, each as its own call (no `cd … &&`, pipes, heredocs or `$(...)` around them, or the call stays sandboxed);
  - anything that starts OpenTofu providers or TFLint plugins (`tofu validate`, `plan`, `providers schema`, `tflint`): their plugin handshake needs Unix sockets. User settings exclude `tofu`, `tflint`, `just infra`, `just validate` and `just tflint`, so run those as their own calls. `just lint` isn't excluded and fails in the sandbox: run `just lint-core` and `just infra` instead (F-0011). A sandboxed `tofu init` also can't write the shared plugin cache: set `TF_PLUGIN_CACHE_DIR` to a directory under `~/.cache` for that call;
  - `dotnet test` (needs Unix sockets and a writable `/tmp`) and anything using containers (Aspire, Azurite, Podman/Docker).
- **.NET in the sandbox:** build multi-project solutions with `dotnet build -m:1` (`just build`); a plain `dotnet build` can hang or fail without an error there. `dotnet test` and the workspace `dotnet format` need Unix sockets: run them as their own calls (`dotnet test --solution Weesite.slnx`, `dotnet format Weesite.slnx --verify-no-changes`), not through `just`. In the sandbox, `dotnet format whitespace --folder --verify-no-changes` plus the build (which enforces code style) is the fallback.
- **Start sessions with `just claude`** (extra arguments are passed through).
- **MCP servers** (`.mcp.json`): Playwright for the browser (navigate, read the accessibility tree, take screenshots; `@playwright/mcp` pinned, fetched by `npx`), Aspire (`aspire agent mcp`) for resources, logs and traces of the app started with `just aspire`. Both run outside the sandbox: Aspire needs Podman for Azurite.
- **Subagents:** give each one its own git worktree and branch, explicit file ownership and its own PR. Shared files (tracker, AGENTS.md) belong to the main agent; subagents propose changes to them in their PR description.
- **Check what loads:** `claude -p --permission-mode plan "list the instruction files in your context"` from the repo root.
