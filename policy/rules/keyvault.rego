# Key Vaults. Defaults are azurerm 5.x's.
package weesite.rules.keyvault

import data.weesite.lib

# Purge protection on (the default is off).
violation contains lib.violation("keyvault-purge-protection", r, "purge_protection_enabled must be true") if {
	some r in lib.of_type("azurerm_key_vault")
	lib.value(r, "purge_protection_enabled") != true
}

# Azure RBAC instead of access policies (azurerm 5.x requires the argument).
violation contains lib.violation("keyvault-rbac", r, "rbac_authorization_enabled must be true") if {
	some r in lib.of_type("azurerm_key_vault")
	lib.value(r, "rbac_authorization_enabled") != true
}
