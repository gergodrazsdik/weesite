# The Azure DNS zone of the site's own domain (prod only; see var.domain).
# The domain's registrar points its name servers at this zone, so the zone
# must outlive everything else: re-creating it would change the name
# servers. The records for the Container App (apex A, asuid TXT, www) and
# its managed certificate come with T-0030 and T-0057.

resource "azurerm_dns_zone" "site" {
  count = var.domain == "" ? 0 : 1

  name                = var.domain
  resource_group_name = data.azurerm_resource_group.env.name
  tags                = local.tags

  lifecycle {
    prevent_destroy = true
  }
}

output "dns_name_servers" {
  description = "Name servers of the DNS zone, to set at the domain's registrar (empty without a domain)."
  value       = try(azurerm_dns_zone.site[0].name_servers, [])
}
