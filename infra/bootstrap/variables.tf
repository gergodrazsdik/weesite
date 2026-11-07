variable "subscription_id" {
  description = "Azure subscription to deploy into."
  type        = string
  nullable    = false

  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.subscription_id))
    error_message = "subscription_id must be a subscription GUID."
  }
}

variable "location" {
  description = "Azure region for every resource, in its short form (for example westeurope). Must be allowed by policy/data/policy.yaml."
  type        = string
  nullable    = false
}

variable "name_suffix" {
  description = "Short suffix that makes the state storage account and Key Vault names globally unique: 3-6 lowercase letters or digits. Changing it replaces both."
  type        = string
  nullable    = false

  validation {
    condition     = can(regex("^[a-z0-9]{3,6}$", var.name_suffix))
    error_message = "name_suffix must be 3-6 lowercase letters or digits."
  }
}

variable "github_owner" {
  description = "GitHub user or organisation that owns the repository."
  type        = string
  nullable    = false
}

variable "github_repository" {
  description = "Repository name, without the owner."
  type        = string
  nullable    = false
}

variable "prod_reviewers" {
  description = "GitHub logins that approve prod deployments (at least one)."
  type        = list(string)
  nullable    = false

  validation {
    condition     = length(var.prod_reviewers) > 0
    error_message = "prod needs at least one reviewer."
  }
}

variable "budget_amount" {
  description = "Monthly budget per environment resource group, in the subscription's billing currency."
  type        = number
  default     = 30
  nullable    = false
}

variable "budget_contact_emails" {
  description = "Addresses that receive the budget alerts (80% of the budget spent, 100% forecast)."
  type        = list(string)
  nullable    = false

  validation {
    condition     = length(var.budget_contact_emails) > 0
    error_message = "Give at least one budget contact address."
  }
}

variable "environment_variables" {
  description = "Extra GitHub environment variables per environment, such as the site's domain: { test = { NAME = \"value\" }, prod = { ... } }."
  type        = map(map(string))
  default     = {}
  nullable    = false

  validation {
    condition     = alltrue([for env in keys(var.environment_variables) : contains(["test", "prod"], env)])
    error_message = "environment_variables keys must be test or prod."
  }
}
