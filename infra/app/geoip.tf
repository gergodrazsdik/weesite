# Visitor countries in the analytics (T-0143): the geoip workflow uploads DB-IP's IP to country
# database each month as country.mmdb into the geoip container (storage.tf), signed in as the
# environment's ops identity (the bootstrap's identities.tf); the web app only reads it, with its
# own blob role (identity.tf). The ops identity's data roles reach the geoip container here and,
# on test, what the seed workflow writes (seed.tf): not the rest of the site's data (T-0177).

# The helper workflows' identity, looked up by name as the apply identity is (signin.tf).
data "azuread_service_principal" "ops" {
  display_name = "id-${local.prefix}-ops-${var.environment}"
}

resource "azurerm_role_assignment" "geoip" {
  scope                = azurerm_storage_container.data["geoip"].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azuread_service_principal.ops.object_id
  principal_type       = "ServicePrincipal"
  description          = "geoip workflow: upload the IP to country database to the geoip container."
}
