# One resource group per environment for infra/app, each with a budget.

resource "azurerm_resource_group" "env" {
  for_each = local.environments

  name     = "rg-${local.workload}-${each.key}"
  location = var.location
  tags     = merge(local.tags, { environment = each.key })
}

resource "azurerm_consumption_budget_resource_group" "env" {
  for_each = local.environments

  name              = "budget-${local.workload}-${each.key}"
  resource_group_id = azurerm_resource_group.env[each.key].id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  # Budgets start on the first of a month; the start date is set once.
  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", plantimestamp())
  }

  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 80
    threshold_type = "Actual"
    contact_emails = var.budget_contact_emails
  }

  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 100
    threshold_type = "Forecasted"
    contact_emails = var.budget_contact_emails
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
