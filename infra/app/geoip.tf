# Visitor countries in the analytics (T-0143): the geoip workflow uploads DB-IP's IP to country
# database each month as country.mmdb into the geoip container (storage.tf), signed in as the
# environment's apply identity; the web app only reads it, with its own blob role (identity.tf).
# This role reaches that one container: the workflow can't read or change the site's content.

resource "azurerm_role_assignment" "geoip" {
  scope                = azurerm_storage_container.data["geoip"].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azuread_service_principal.apply.object_id
  principal_type       = "ServicePrincipal"
  description          = "geoip workflow: upload the IP to country database to the geoip container."
}
