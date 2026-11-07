# infra/bootstrap

**Placeholder.** A one-off OpenTofu root, run locally once per deployment (`just bootstrap-plan`, `just bootstrap-apply`, then `just bootstrap-migrate`). It comes as the first infrastructure task (see the [tracker](../../docs/plan/TRACKER.md)) and follows the [brief](../../docs/design/brief.md#repo-cicd-and-environments).

What it will create:

- **State:** a resource group with a storage account (blob versioning, soft delete, Entra ID auth only, shared keys off) and one state container per environment.
- **Encryption:** a Key Vault (RBAC, purge protection) with one key per environment for OpenTofu's client-side state encryption.
- **Environments:** one resource group each for `test` and `prod`.
- **Identities:** a read-only plan identity federated on pull requests and `main`, and one apply identity per environment federated on `environment:test` and `environment:prod`. Each apply identity gets Contributor on its own resource group, plus Role Based Access Control Administrator limited by a condition to the Storage Blob, Table and Queue data roles the site's managed identity needs. Federated subjects use GitHub's immutable `repo:<owner>@<id>/<repo>@<id>` form.
- **GitHub** (through the OpenTofu GitHub provider): the `test` and `prod` environments with their protection rules (`prod` needs approval and deploys only from `main`), Azure IDs as environment secrets, domain and hostnames as environment variables. These environments are not created by hand.
- **Budget alert** on both environment resource groups (default $30/month, configurable).

Inputs come from a gitignored `bootstrap.auto.tfvars` (a commented `.example` will be committed); the GitHub token from an environment variable. The first apply uses local state; `just bootstrap-migrate` then moves it into the storage account it created, in its own container and encrypted with its own key.
