package weesite.tests.keyvault

import data.weesite.fixtures

good_vault := object.union(fixtures.base, {
	"purge_protection_enabled": true,
	"rbac_authorization_enabled": true,
})

test_compliant_vault_passes if {
	fixtures.rules_both("azurerm_key_vault", good_vault) == {set()}
}

test_deprecated_rbac_argument_passes if {
	vault := object.union(object.remove(good_vault, ["rbac_authorization_enabled"]), {"enable_rbac_authorization": true})
	fixtures.rules_both("azurerm_key_vault", vault) == {set()}
}

test_purge_protection_default_fails if {
	fixtures.rules_both("azurerm_key_vault", object.remove(good_vault, ["purge_protection_enabled"])) == {{"keyvault-purge-protection"}}
}

test_access_policies_fail if {
	fixtures.rules_both("azurerm_key_vault", object.union(good_vault, {"rbac_authorization_enabled": false})) == {{"keyvault-rbac"}}
}

test_rbac_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_key_vault", "this", object.union(good_vault, {"rbac_authorization_enabled": "${var.rbac}"}))
	fixtures.rules_for(doc, []) == set()
}
