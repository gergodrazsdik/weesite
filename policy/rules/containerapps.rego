# Container Apps and Container Apps jobs.
package weesite.rules.containerapps

import data.weesite.lib

workload_types := {"azurerm_container_app", "azurerm_container_app_job"}

# Ingress over HTTPS only.
violation contains lib.violation("container-app-https-only", r, "ingress allow_insecure_connections must not be true") if {
	some r in lib.of_type("azurerm_container_app")
	some ingress in lib.blocks(r, "ingress")
	object.get(ingress, "allow_insecure_connections", null) == true
}

# Workloads reach Azure with a managed identity...
violation contains lib.violation("managed-identity", r, "add an identity block (managed identity)") if {
	some type in workload_types
	some r in lib.of_type(type)
	count(lib.blocks(r, "identity")) == 0
}

# ...and pull from registries with it, not with a password.
violation contains lib.violation("managed-identity", r, msg) if {
	some type in workload_types
	some r in lib.of_type(type)
	some registry in lib.blocks(r, "registry")
	object.get(registry, "password_secret_name", null) != null
	msg := sprintf("registry %v uses a password; set identity instead", [object.get(registry, "server", "")])
}

# Secrets are Key Vault references, never values stored in the app (and
# in the OpenTofu state).
violation contains lib.violation("no-stored-secrets", r, msg) if {
	some type in workload_types
	some r in lib.of_type(type)
	some secret in lib.blocks(r, "secret")
	object.get(secret, "value", null) != null
	msg := sprintf("secret %v has a value; use key_vault_secret_id", [object.get(secret, "name", "")])
}
