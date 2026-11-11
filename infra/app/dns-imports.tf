# One-off (T-0121): adopts email records that were created in the portal before PROD_DNS_RECORDS
# could hold MX records, so the apply takes them over instead of failing on "already exists".
# Only Microsoft 365's standard record names, and only those the variable lists. Remove this file
# once the prod apply has run: an import of a record that doesn't exist fails.

locals {
  dns_adopt = var.domain == "" ? { mx = [], txt = [], cname = [] } : {
    mx    = [for name in ["@"] : name if contains(keys(var.dns_records.mx), name)]
    txt   = [for name in ["_dmarc"] : name if contains(keys(var.dns_records.txt), name)]
    cname = [for name in ["selector1._domainkey", "selector2._domainkey", "autodiscover"] : name if contains(keys(var.dns_records.cname), name)]
  }
}

import {
  for_each = toset(local.dns_adopt.mx)
  to       = azurerm_dns_mx_record.extra[each.key]
  id       = "${azurerm_dns_zone.site[0].id}/MX/${each.key}"
}

import {
  for_each = toset(local.dns_adopt.txt)
  to       = azurerm_dns_txt_record.extra[each.key]
  id       = "${azurerm_dns_zone.site[0].id}/TXT/${each.key}"
}

import {
  for_each = toset(local.dns_adopt.cname)
  to       = azurerm_dns_cname_record.extra[each.key]
  id       = "${azurerm_dns_zone.site[0].id}/CNAME/${each.key}"
}
