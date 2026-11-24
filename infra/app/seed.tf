# The sample site's seeder (T-0128): the seed workflow, run by hand on test, uploads seed/ into the
# storage account as the environment's ops identity (geoip.tf), then asks for a re-embed. These
# data roles let it do that, on the containers seed/ fills and the re-index queue only (T-0177);
# prod never gets them, and never loads the seed.

locals {
  seeder_roles = local.prod ? {} : {
    content  = { scope = azurerm_storage_container.data["content"].id, role = "Storage Blob Data Contributor" }
    media    = { scope = azurerm_storage_container.data["media"].id, role = "Storage Blob Data Contributor" }
    settings = { scope = azurerm_storage_container.data["settings"].id, role = "Storage Blob Data Contributor" }
    reindex  = { scope = azurerm_storage_queue.reindex.id, role = "Storage Queue Data Message Sender" }
  }
}

resource "azurerm_role_assignment" "seeder" {
  for_each = local.seeder_roles

  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = data.azuread_service_principal.ops.object_id
  principal_type       = "ServicePrincipal"
  description          = "Seed workflow (test only): ${each.value.role} on ${each.key}."
}
