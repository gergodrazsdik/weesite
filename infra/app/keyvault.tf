# App secrets (sign-in client secrets and the like), read by the web app as Key Vault references.

resource "azurerm_key_vault" "app" {
  #checkov:skip=CKV_AZURE_189:Public network access is needed without a VNet; RBAC only.
  #checkov:skip=CKV_AZURE_109:No firewall rules; see CKV_AZURE_189.
  #checkov:skip=CKV2_AZURE_32:No VNet or private endpoints; see CKV_AZURE_189.
  name                       = "kv-${local.workload}-${var.environment}-${local.suffix}"
  resource_group_name        = data.azurerm_resource_group.env.name
  location                   = local.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = local.tags

  lifecycle {
    prevent_destroy = true
  }
}

# Wraps the web app's Data Protection keys (sign-in cookies, antiforgery tokens) before they're
# stored in Blob (T-0078). The site can only wrap and unwrap with it (Key Vault Crypto User on
# this key, identity.tf); the apply identity creates it (Key Vault Crypto Officer, bootstrap).
resource "azurerm_key_vault_key" "dataprotection" {
  #checkov:skip=CKV_AZURE_40:No expiry: keys wrapped under an expired version could no longer be unwrapped (F-0012).
  #checkov:skip=CKV_AZURE_112:Software-protected keys (Standard SKU); HSM keys need Premium for no real gain here.
  name         = "dataprotection"
  key_vault_id = azurerm_key_vault.app.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]
  tags         = local.tags

  # Losing it signs every editor out and voids open forms; nothing else.
  lifecycle {
    prevent_destroy = true
  }
}

data "azurerm_client_config" "current" {}

# The plan identity that reads this environment refreshes the secrets OpenTofu writes here: the
# Microsoft sign-in client (signin.tf) and the Application Insights connection string
# (monitoring.tf). It reads those, each at its own scope, and no other secret in the vault: not
# the ones stored by hand, such as the Google client, SMTP and the admin list (T-0177). Pull
# requests plan test with id-<prefix>-plan, in the plan environment after an approval; prod is
# planned only from main, with id-<prefix>-plan-main (the bootstrap's identities.tf).
data "azuread_service_principal" "plan" {
  display_name = local.prod ? "id-${local.prefix}-plan-main" : "id-${local.prefix}-plan"
}

locals {
  plan_secrets = {
    "Authentication--Microsoft--ClientId"     = azurerm_key_vault_secret.signin["Authentication--Microsoft--ClientId"].resource_versionless_id
    "Authentication--Microsoft--ClientSecret" = azurerm_key_vault_secret.signin["Authentication--Microsoft--ClientSecret"].resource_versionless_id
    "appinsights-connection-string"           = azurerm_key_vault_secret.appinsights.resource_versionless_id
  }
}

resource "azurerm_role_assignment" "plan_secrets" {
  for_each = local.plan_secrets

  scope                = each.value
  role_definition_name = "Key Vault Secrets User"
  principal_id         = data.azuread_service_principal.plan.object_id
  principal_type       = "ServicePrincipal"
  description          = "Plan: refresh the secret ${each.key}."
}
