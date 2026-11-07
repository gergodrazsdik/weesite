# The bootstrap's own state lives in the storage account it creates,
# encrypted with its own key in the Key Vault it creates. The first run
# can't use either, so it runs with local, unencrypted state from a
# gitignored first_run_override.tf (`just bootstrap-first-run`), and
# `just bootstrap-migrate` moves the state here afterwards.
terraform {
  backend "azurerm" {
    resource_group_name  = local.state_resource_group_name
    storage_account_name = local.state_storage_account_name
    container_name       = "bootstrap"
    key                  = "bootstrap.tfstate"
    subscription_id      = var.subscription_id
    use_azuread_auth     = true
  }

  encryption {
    key_provider "azure_vault" "state" {
      vault_uri      = local.state_key_vault_uri
      vault_key_name = "bootstrap"
      key_length     = 32
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
