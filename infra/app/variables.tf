# In CI these come from the environment: TF_VAR_environment from the job,
# the state_* values from the repository variables the bootstrap sets.

variable "environment" {
  description = "Environment to plan or apply: test or prod."
  type        = string
  nullable    = false

  validation {
    condition     = contains(["test", "prod"], var.environment)
    error_message = "environment must be test or prod."
  }
}

variable "state_resource_group" {
  description = "Resource group of the state storage (repository variable TF_STATE_RESOURCE_GROUP)."
  type        = string
  nullable    = false
}

variable "state_storage_account" {
  description = "State storage account (repository variable TF_STATE_STORAGE_ACCOUNT)."
  type        = string
  nullable    = false
}

variable "state_key_vault_uri" {
  description = "Key Vault with the state encryption keys (repository variable TF_STATE_KEY_VAULT_URI)."
  type        = string
  nullable    = false
}

variable "use_oidc" {
  description = "Authenticate the state encryption key provider with GitHub's OIDC token (CI); otherwise the Azure CLI."
  type        = bool
  default     = false
  nullable    = false
}

variable "domain" {
  description = "The site's own domain, served from an Azure DNS zone in this environment's resource group. Empty: no zone. scripts/infra-plan.sh sets it for prod from the repository variable PROD_DOMAIN; test uses the generated Container Apps hostname."
  type        = string
  default     = ""
  nullable    = false

  validation {
    condition     = var.domain == "" || can(regex("^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", var.domain))
    error_message = "domain must be a lowercase domain name such as example.com, or empty."
  }
}

variable "app_settings" {
  description = "Extra app settings for this environment, as environment variable names (Search__EmbedLimit for Search:EmbedLimit) and values. Comes from the repository variable APP_SETTINGS_TEST or APP_SETTINGS_PROD (scripts/infra-plan.sh); src/Weesite.Web/appsettings.example.json lists the settings. Not for secrets: those go in the environment's Key Vault."
  type        = map(string)
  default     = {}
  nullable    = false

  validation {
    condition     = alltrue([for name in keys(var.app_settings) : can(regex("^[A-Za-z][A-Za-z0-9_]*$", name))])
    error_message = "App setting names are environment variable names: letters, digits and underscores, with __ between sections (Search__EmbedLimit)."
  }

  validation {
    condition = length(setintersection(keys(var.app_settings), [
      "AZURE_CLIENT_ID", "APPLICATIONINSIGHTS_CONNECTION_STRING", "ConnectionStrings__blobs", "ConnectionStrings__tables",
      "ConnectionStrings__queues", "Seed__Path", "KeyVault__Uri", "KeyVault__SignInSecretVersion", "DataProtection__KeyId", "Site__NoIndex",
    ])) == 0
    error_message = "These settings are set by infra/app itself and can't be overridden: AZURE_CLIENT_ID, APPLICATIONINSIGHTS_CONNECTION_STRING, ConnectionStrings__*, Seed__Path, KeyVault__*, DataProtection__KeyId, Site__NoIndex."
  }
}
