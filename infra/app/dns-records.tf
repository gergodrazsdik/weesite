# Records in the site's DNS zone that come from the deployment rather than from Weesite (T-0073),
# such as Google Search Console's TXT and Bing Webmaster Tools' CNAME for verification: both
# services check them again from time to time, so they stay. var.dns_records, from the repository
# variable PROD_DNS_RECORDS. The site's own records are in domain.tf.

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
