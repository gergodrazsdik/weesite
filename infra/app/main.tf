# The site's resources (T-0030) go here. For now the root only reads the
# environment's resource group, which proves the identities, the state
# access and the encryption keys work in CI.

provider "azurerm" {
  storage_use_azuread = true

  # The bootstrap registers the resource providers; the apply identities
  # can't, as they only reach their resource group.
  resource_provider_registrations = "none"

  features {}
}

locals {
  workload = "weesite"
}

data "azurerm_resource_group" "env" {
  name = "rg-${local.workload}-${var.environment}"
}

output "resource_group_name" {
  description = "The environment's resource group."
  value       = data.azurerm_resource_group.env.name
}
