# The site on its own domain (T-0057; prod only, see var.domain): the apex and www point at the
# web app, each gets a free managed certificate, and the app answers www with a permanent
# redirect to the apex (Site:BaseUrl, containerapp.tf).
#
# Container Apps needs this order: the DNS records, then the hostname on the app (with no
# certificate), then the managed certificate, then the binding. azurerm does the first three;
# it can't bind a managed certificate (hashicorp/terraform-provider-azurerm#27362), so azapi
# does. The certificates renew by themselves while the records stay as they are: an A
# record for the apex, a direct CNAME for www.

locals {
  # Each hostname with its DNS label and how its certificate's domain is validated: the apex can
  # only be validated over HTTP, a subdomain through its CNAME.
  hostnames = var.domain == "" ? {} : {
    apex = { name = var.domain, label = "@", validation = "HTTP" }
    www  = { name = "www.${var.domain}", label = "www", validation = "CNAME" }
  }
}

resource "azurerm_dns_a_record" "apex" {
  count = var.domain == "" ? 0 : 1

  name                = "@"
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  records             = [azurerm_container_app_environment.main.static_ip_address]
  tags                = local.tags
}

resource "azurerm_dns_cname_record" "www" {
  count = var.domain == "" ? 0 : 1

  name                = "www"
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  record              = azurerm_container_app.web.ingress[0].fqdn
  tags                = local.tags
}

# Proves to Container Apps that the domain is ours: asuid for the apex, asuid.www for www.
resource "azurerm_dns_txt_record" "asuid" {
  for_each = local.hostnames

  name                = each.value.label == "@" ? "asuid" : "asuid.${each.value.label}"
  zone_name           = azurerm_dns_zone.site[0].name
  resource_group_name = data.azurerm_resource_group.env.name
  ttl                 = 3600
  tags                = local.tags

  record {
    value = azurerm_container_app.web.custom_domain_verification_id
  }
}

# The hostname on the app, without a certificate: Container Apps issues one only for a hostname
# an app already has. The binding (below) changes these two attributes outside azurerm.
resource "azurerm_container_app_custom_domain" "site" {
  for_each = local.hostnames

  name             = each.value.name
  container_app_id = azurerm_container_app.web.id

  lifecycle {
    ignore_changes = [certificate_binding_type, container_app_environment_certificate_id]
  }

  depends_on = [azurerm_dns_txt_record.asuid, azurerm_dns_a_record.apex, azurerm_dns_cname_record.www]
}

# Free certificates issued and renewed by Container Apps (DigiCert); issuing takes a few minutes.
resource "azurerm_container_app_environment_managed_certificate" "site" {
  for_each = local.hostnames

  name                         = "cert-${replace(each.value.name, ".", "-")}"
  container_app_environment_id = azurerm_container_app_environment.main.id
  subject_name                 = each.value.name
  domain_control_validation    = each.value.validation
  tags                         = local.tags

  depends_on = [azurerm_container_app_custom_domain.site]
}

# Binds each certificate to its hostname. azapi reads the app, merges this in (custom domains
# matched by name) and writes it back, and puts it back if the binding drifts. Removing a domain
# later means removing its binding first: a bound certificate can't be deleted.
resource "azapi_update_resource" "custom_domain_bindings" {
  count = var.domain == "" ? 0 : 1

  type        = "Microsoft.App/containerApps@2025-07-01"
  resource_id = azurerm_container_app.web.id

  body = {
    properties = {
      configuration = {
        ingress = {
          customDomains = [
            for key, host in local.hostnames : {
              name          = host.name
              bindingType   = "SniEnabled"
              certificateId = azurerm_container_app_environment_managed_certificate.site[key].id
            }
          ]
        }
      }
    }
  }
}

output "site_url" {
  description = "The site's canonical address: https:// and the domain, or the Container Apps hostname without one."
  value       = var.domain == "" ? "https://${azurerm_container_app.web.ingress[0].fqdn}" : "https://${var.domain}"
}
