@AGENTS.md

## Claude Code

Additions for Claude Code only; everything shared is in AGENTS.md (imported above).

- **Sandbox:** `.claude/settings.json` turns on the Bash sandbox with a network allowlist (GitHub, NuGet, .NET, OpenTofu registry, Microsoft container registry) and denies reading secrets, state and `*.auto.tfvars`. Some things can't run inside it and run as separate calls outside, as your user-level setup allows:
  - commit signing and `gh`: commit with `git commit -s -F <file>` and open PRs with `gh pr create --body-file <file>`, each as its own call (no `cd … &&`, pipes, heredocs or `$(...)` around them, or the call stays sandboxed);
  - anything that starts OpenTofu providers or TFLint plugins (`tofu validate`, `plan`, `providers schema`, `tflint`): their plugin handshake needs Unix sockets. Run `tofu -chdir=infra/<root> validate` as its own call (user settings exclude `tofu`); `tflint --recursive --config "$PWD/.tflint.hcl"` isn't excluded and needs an approved run outside the sandbox. `just infra` (and `just lint`, `validate`, `tflint`) start them from inside the sandbox and fail there; `just lint-core` works (F-0011). A sandboxed `tofu init` also can't write the shared plugin cache: set `TF_PLUGIN_CACHE_DIR` to a directory under `~/.cache` for that call;
  - `dotnet test` (needs Unix sockets and a writable `/tmp`) and anything using containers (Aspire, Azurite, Podman/Docker).
- **.NET in the sandbox:** build multi-project solutions with `dotnet build -m:1`; a plain `dotnet build` can hang or fail without an error there.
- **Start sessions with `just claude`** (extra arguments are passed through).
- **MCP servers** (added with the code, in `.mcp.json`): Playwright for the browser (navigate, read the accessibility tree, take screenshots), Aspire for resources, logs and traces of the running app.
- **Subagents:** give each one its own git worktree and branch, explicit file ownership and its own PR. Shared files (tracker, CHANGELOG, AGENTS.md) belong to the main agent; subagents propose changes to them in their PR description.
- **Check what loads:** `claude -p --permission-mode plan "list the instruction files in your context"` from the repo root.
