# 0004: Sign-in registrations and secrets

- Status: accepted
- Date: 2026-11-09
- Tracker: T-0046

## Context and problem statement

The [brief](../design/brief.md) puts sign-in in the app: Google and Microsoft accounts through ASP.NET Core's handlers, an allowlist and roles. Each environment needs an app registration with each provider, with redirect URIs on that environment's hostname and a client secret the app can read. The maintainer asked for the registrations to be managed by OpenTofu wherever possible.

## Decision outcome

| Topic | Decision | Why |
| --- | --- | --- |
| Microsoft registration | One Entra app per environment in `infra/app` (`azuread`), personal and work accounts, redirect URIs on the environment's hostname (test also `http://localhost:5080` for local development); its client secret rotates every 180 days | the redirect URIs need the Container App's hostname, which only `infra/app` knows; OpenTofu owns the whole lifecycle |
| Google registration | Created by hand in the Google Auth Platform console, one OAuth client per environment ([docs/deploy.md](../deploy.md)) | Google offers no API for OAuth clients: the IAP OAuth Admin API, the last one, shut down in March 2026 |
| Where secrets live | Each environment's Key Vault, as `Authentication--Microsoft--ClientId`/`ClientSecret` (written by OpenTofu) and `Authentication--Google--ClientId`/`ClientSecret` (written by the maintainer with `just google-signin <env>`) | one place per environment; nothing in GitHub or the image |
| How the app reads them | The Key Vault configuration provider at startup (`KeyVault__Uri`, the site's managed identity); a provider whose secrets are missing stays off | an environment works before its Google client exists, and no deploy fails on a missing secret |
| Permissions (bootstrap) | Apply identities: Graph `Application.ReadWrite.OwnedBy` and Key Vault Secrets Officer on their resource group. Plan identity: Graph `Application.Read.All` and Key Vault Secrets User, to refresh. The maintainer: Key Vault Secrets Officer, for the Google secrets | least privilege: an apply identity manages only the apps it created. The plan identity can already decrypt the state, which holds the same secret values |
| Allowlist | Admins from configuration (`Site__Admins`), further editors and their roles on an admin page, kept in Table Storage with the audit log | the maintainer can't lock themselves out; adding an author needs no deploy |

### Consequences

- The bootstrap needs the `azuread` provider, and its first apply after this change needs the maintainer to be able to grant Graph application permissions (Global Administrator or Privileged Role Administrator in the tenant).
- A new Google client secret takes effect at the next restart or revision.
- When the Microsoft secret rotates, the same apply writes the new Key Vault version and rolls the Container App to a new revision (the version is one of its settings), and only then deletes the old password, so sign-in never sees a stale secret.
