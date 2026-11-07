# Key Vaults. Defaults are azurerm 4.x's.
package weesite.rules.keyvault

import data.weesite.lib

# Purge protection on (the default is off).
violation contains lib.violation("keyvault-purge-protection", r, "purge_protection_enabled must be true") if {
	some r in lib.of_type("azurerm_key_vault")
	lib.value(r, "purge_protection_enabled") != true
}

# Azure RBAC instead of access policies (the default is access policies).
# azurerm 4.x renamed enable_rbac_authorization to rbac_authorization_enabled;
# either one set to true passes.
violation contains lib.violation("keyvault-rbac", r, "rbac_authorization_enabled must be true") if {
	some r in lib.of_type("azurerm_key_vault")
	not rbac_enabled(r)
	not rbac_unknown(r)
}

rbac_enabled(r) if lib.value(r, "rbac_authorization_enabled") == true

rbac_enabled(r) if lib.value(r, "enable_rbac_authorization") == true

rbac_unknown(r) if lib.unknown(r, "rbac_authorization_enabled")

rbac_unknown(r) if lib.unknown(r, "enable_rbac_authorization")
