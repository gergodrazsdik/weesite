# Storage accounts and containers. Defaults are azurerm 5.x's.
package weesite.rules.storage

import data.weesite.lib

# HTTPS only (the default; flag it being turned off).
violation contains lib.violation("storage-https-only", r, "https_traffic_only_enabled must not be false") if {
	some r in lib.of_type("azurerm_storage_account")
	lib.value(r, "https_traffic_only_enabled") == false
}

# TLS 1.2 or later (the default is TLS1_2).
violation contains lib.violation("storage-min-tls", r, msg) if {
	some r in lib.of_type("azurerm_storage_account")
	v := lib.value(r, "min_tls_version")
	v != null
	not v in {"TLS1_2", "TLS1_3"}
	msg := sprintf("min_tls_version is %v; use TLS1_2 or later", [v])
}

# No anonymous blob access: the account must not allow it (the default
# forbids it), and no container may ask for it.
violation contains lib.violation("storage-no-public-blob", r, "allow_nested_items_to_be_public must not be true") if {
	some r in lib.of_type("azurerm_storage_account")
	lib.value(r, "allow_nested_items_to_be_public") == true
}

violation contains lib.violation("storage-no-public-blob", r, msg) if {
	some r in lib.of_type("azurerm_storage_container")
	v := lib.value(r, "container_access_type")
	not v in {null, "private"}
	msg := sprintf("container_access_type is %v; use private", [v])
}

# Entra ID auth only: shared keys must be off explicitly (the default is on).
violation contains lib.violation("storage-shared-key-off", r, "shared_access_key_enabled must be false") if {
	some r in lib.of_type("azurerm_storage_account")
	lib.value(r, "shared_access_key_enabled") != false
}
