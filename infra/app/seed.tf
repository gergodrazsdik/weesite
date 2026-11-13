# The sample site's seeder (T-0128): the seed workflow, run by hand on test, uploads seed/ into the
# storage account as the environment's apply identity, then asks for a re-embed. These data roles
# let it do that; prod never gets them, and never loads the seed.

locals {
  seeder_roles = local.prod ? {} : {
    blob  = "Storage Blob Data Contributor"
    queue = "Storage Queue Data Message Sender"
  }
}

resource "azurerm_role_assignment" "seeder" {
  for_each = local.seeder_roles

  scope                = azurerm_storage_account.data.id
  role_definition_name = each.value
  principal_id         = data.azuread_service_principal.apply.object_id
  principal_type       = "ServicePrincipal"
  description          = "Seed workflow (test only): ${each.value}."
}
