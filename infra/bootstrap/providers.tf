# Azure: signs in with the Azure CLI (`az login`) of whoever runs the
# bootstrap. GitHub: a token in GITHUB_TOKEN with admin rights on the
# repository (the bootstrap recipes fall back to `gh auth token`).

provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true

  # azurerm 5.x registers nothing by default. The bootstrap registers what
  # it and infra/app use, because the app's apply identities can only
  # manage their own resource group.
  resource_providers_to_register = [
    "Microsoft.App",
    "Microsoft.Consumption",
    "Microsoft.Insights",
    "Microsoft.KeyVault",
    "Microsoft.ManagedIdentity",
    "Microsoft.Network",
    "Microsoft.OperationalInsights",
    "Microsoft.Storage",
  ]

  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
      recover_soft_deleted_keys       = true
    }

    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

provider "github" {
  owner = var.github_owner
}
