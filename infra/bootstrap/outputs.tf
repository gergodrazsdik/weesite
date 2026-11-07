output "state_resource_group_name" {
  description = "Resource group of the state storage, Key Vault and identities."
  value       = azurerm_resource_group.state.name
}

output "state_storage_account_name" {
  description = "Storage account with one state container per environment (and bootstrap)."
  value       = azurerm_storage_account.state.name
}

output "state_key_vault_uri" {
  description = "Key Vault with one state encryption key per environment (and bootstrap)."
  value       = local.state_key_vault_uri
}

output "environment_resource_groups" {
  description = "Resource group per environment, for infra/app."
  value       = { for env, rg in azurerm_resource_group.env : env => rg.name }
}

output "plan_client_id" {
  description = "Client ID of the read-only plan identity."
  value       = azurerm_user_assigned_identity.plan.client_id
}

output "apply_client_ids" {
  description = "Client ID of the apply identity per environment."
  value       = { for env, id in azurerm_user_assigned_identity.apply : env => id.client_id }
}

output "github_oidc_subject" {
  description = "Subject prefix the federated credentials trust."
  value       = local.github_subject
}
