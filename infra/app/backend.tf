# One state per environment: the environment's container in the bootstrap's
# storage account, encrypted with the environment's key. Authentication
# comes from the ARM_* environment (ARM_USE_OIDC=true in CI) or the Azure
# CLI. The plan identity can only read the state, so plans use -lock=false.
terraform {
  backend "azurerm" {
    resource_group_name  = var.state_resource_group
    storage_account_name = var.state_storage_account
    container_name       = var.environment
    key                  = "app.tfstate"
    use_azuread_auth     = true
  }

  encryption {
    key_provider "azure_vault" "state" {
      vault_uri      = var.state_key_vault_uri
      vault_key_name = var.environment
      key_length     = 32
      use_oidc       = var.use_oidc
    }

    method "aes_gcm" "state" {
      keys = key_provider.azure_vault.state
    }

    state {
      method = method.aes_gcm.state
    }

    plan {
      method = method.aes_gcm.state
    }
  }
}
