# Is the site up (brief, Monitoring; T-0054)? prod only: an Application Insights standard test
# loads the home page from two European locations every 15 minutes (about $3.70 a month at
# $0.000645 a run; test scales to zero, and pings would keep it running). When both locations
# fail, the subscription's Owners get an email, so no address is configured anywhere.

locals {
  # The site's own domain once it has one (domain.tf), so the test also checks its certificate.
  availability = local.prod ? { home = "${var.domain == "" ? "https://${azurerm_container_app.web.ingress[0].fqdn}" : "https://${var.domain}"}/" } : {}
}

resource "azurerm_application_insights_standard_web_test" "home" {
  for_each = local.availability

  name                    = "awt-${local.workload}-${var.environment}-${each.key}"
  resource_group_name     = data.azurerm_resource_group.env.name
  location                = local.location
  application_insights_id = azurerm_application_insights.web.id
  description             = "Loads the home page, on the site's own domain when it has one."
  geo_locations           = ["emea-nl-ams-azr", "emea-gb-db3-azr"]
  frequency               = 900
  timeout                 = 30
  enabled                 = true
  retry_enabled           = true

  # Azure links a web test to its Application Insights resource with this tag; without it every
  # plan would show the tag being removed.
  tags = merge(local.tags, { "hidden-link:${azurerm_application_insights.web.id}" = "Resource" })

  request {
    url = each.value
  }

  validation_rules {
    expected_status_code        = 200
    ssl_check_enabled           = true
    ssl_cert_remaining_lifetime = 7
  }
}

data "azurerm_role_definition" "owner" {
  name = "Owner"
}

resource "azurerm_monitor_action_group" "owners" {
  for_each = local.availability

  name                = "ag-${local.workload}-${var.environment}-owners"
  resource_group_name = data.azurerm_resource_group.env.name
  short_name          = local.workload
  tags                = local.tags

  # Emails whoever holds Owner on the subscription.
  arm_role_receiver {
    name                    = "subscription-owners"
    role_id                 = data.azurerm_role_definition.owner.role_definition_id
    use_common_alert_schema = true
  }
}

resource "azurerm_monitor_metric_alert" "home" {
  for_each = local.availability

  name                = "alert-${local.workload}-${var.environment}-${each.key}-down"
  resource_group_name = data.azurerm_resource_group.env.name
  scopes              = [azurerm_application_insights_standard_web_test.home[each.key].id, azurerm_application_insights.web.id]
  description         = "The site's home page failed to load from both test locations."
  severity            = 1
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = local.tags

  application_insights_web_test_location_availability_criteria {
    web_test_id           = azurerm_application_insights_standard_web_test.home[each.key].id
    component_id          = azurerm_application_insights.web.id
    failed_location_count = 2
  }

  action {
    action_group_id = azurerm_monitor_action_group.owners[each.key].id
  }
}
