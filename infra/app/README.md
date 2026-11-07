# infra/app

**Placeholder.** The site's own resources, one OpenTofu state per environment (`test`, `prod`), applied by the `infra-apply` workflow after approval. Planned modules (see the [brief](../../docs/design/brief.md#hosting-decision) and the [tracker](../../docs/plan/TRACKER.md)):

- Storage (Blob for content, vectors and media; Table for analytics and the audit log; a Queue for the re-index job), with soft delete and versioning
- Container Apps environment, the web app and the re-index job; OpenTofu ignores the image field so app deploys and applies don't undo each other
- Azure DNS zone, custom domains and managed certificates (prod only)
- Key Vault for app secrets, Application Insights with a daily cap, an availability test

Deployment-specific values (domain, hostnames) come from GitHub environment variables as `TF_VAR_*`, never from committed files. Resource names follow the Cloud Adoption Framework's naming and abbreviations.
