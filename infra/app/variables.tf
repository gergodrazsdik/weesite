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
