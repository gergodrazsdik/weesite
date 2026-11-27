# Agent instructions: src/Weesite.Web/

The ASP.NET Core 11 app (Blazor static SSR), also the re-index job (`reindex`) and the seed command (`seed`). The rules in the root [AGENTS.md](../../AGENTS.md) apply; these add to them.

- **Blazor forms:** handle a post in the `EditForm`'s `OnSubmit`, not in `OnInitializedAsync`; navigating before the form has rendered turns the redirect into a 400 (F-0046). Keep the explicit `app.UseAntiforgery()`; only components can set TempData.
- **Key Vault secrets load in the background** (T-0192): anything that reads a secret-backed setting (sign-in clients, `Site:Admins`, SMTP) waits on `SecretsGate`: its path in `SecretsGate.Paths` (`BackgroundSecrets.cs`) or `await WhenLoadedAsync`. A background service skips a tick on `SecretsUnavailableException`; throwing would stop the host.
- **Startup logs:** Application Insights loses what's logged before the host starts, so a line from startup work logs on `ApplicationStarted`, and a command's host keeps only the telemetry's hosted services (`KeepOnlyTelemetryHostedServices`, `StartForTelemetryAsync`). `StartupLogTests` fails otherwise (F-0264).
- **Strict CSP**, no inline scripts or styles; the public site works without JavaScript. Admin scripts are ES modules and `ws-` web components, checked by `scripts/check-js.sh`.
- **Caching:** the output cache holds anonymous GETs per theme and content version; responses with antiforgery tokens are uncacheable, and 404/410 get `no-store`. A new route segment needs a reserved slug, so content can't shadow it.
- **Settings** read by name must be listed in `appsettings.example.json` (a test checks); UI strings go in `Resources/` (`PublicResource*.resx`, `AdminResource.resx`). More: [engineering notes](../../docs/engineering-notes.md#blazor-static-ssr-and-the-admin).
