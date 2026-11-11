# Records in the site's DNS zone that come from the deployment rather than from Weesite: the
# search engines' verification (T-0073), which they check again from time to time, and email
# (T-0121: MX, SPF, DKIM, DMARC). var.dns_records, from the repository variable PROD_DNS_RECORDS.
# Each resource owns a whole record set (a name and type), so a value added to one in the portal
# is removed by the next apply: add it to the variable instead. The site's own records are in
# domain.tf.

resource "azurerm_dns_txt_record" "extra" {
  for_each = var.domain == "" ? {} : var.dns_records.txt

  name                = each.key
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  tags                = local.tags

  dynamic "record" {
    for_each = toset(each.value)

    content {
      value = record.value
    }
  }
}

resource "azurerm_dns_cname_record" "extra" {
  for_each = var.domain == "" ? {} : var.dns_records.cname

  name                = each.key
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  record              = each.value
  tags                = local.tags
}

resource "azurerm_dns_mx_record" "extra" {
  for_each = var.domain == "" ? {} : var.dns_records.mx

  name                = each.key
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  tags                = local.tags

  dynamic "record" {
    for_each = each.value

    content {
      preference = tostring(record.value.preference)
      exchange   = record.value.exchange
    }
  }
}
