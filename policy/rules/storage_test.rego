package weesite.tests.storage

import data.weesite.fixtures

good_account := object.union(fixtures.base, {
	"allow_nested_items_to_be_public": false,
	"shared_access_key_enabled": false,
	"min_tls_version": "TLS1_2",
})

account(overrides) := object.union(good_account, overrides)

test_compliant_account_passes if {
	fixtures.rules_both("azurerm_storage_account", good_account) == {set()}
}

test_https_disabled_fails if {
	fixtures.rules_both("azurerm_storage_account", account({"https_traffic_only_enabled": false})) == {{"storage-https-only"}}
}

test_old_tls_fails if {
	fixtures.rules_both("azurerm_storage_account", account({"min_tls_version": "TLS1_1"})) == {{"storage-min-tls"}}
}

test_default_tls_passes if {
	fixtures.rules_both("azurerm_storage_account", object.remove(good_account, ["min_tls_version"])) == {set()}
}

# allow_nested_items_to_be_public and shared_access_key_enabled default to
# true, so leaving them out fails.
test_public_blob_default_fails if {
	fixtures.rules_both("azurerm_storage_account", object.remove(good_account, ["allow_nested_items_to_be_public"])) == {{"storage-no-public-blob"}}
}

test_shared_key_default_fails if {
	fixtures.rules_both("azurerm_storage_account", object.remove(good_account, ["shared_access_key_enabled"])) == {{"storage-shared-key-off"}}
}

test_public_container_fails if {
	fixtures.rules_both("azurerm_storage_container", {"container_access_type": "blob"}) == {{"storage-no-public-blob"}}
}

test_private_container_passes if {
	fixtures.rules_both("azurerm_storage_container", {"container_access_type": "private"}) == {set()}
}

# An expression in HCL, or a value computed during apply, isn't judged.
test_hcl_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_storage_account", "this", account({"shared_access_key_enabled": "${var.shared_keys}"}))
	fixtures.rules_for(doc, []) == set()
}

test_plan_unknown_is_not_judged if {
	doc := fixtures.plan_change(
		"azurerm_storage_account", "this",
		object.remove(good_account, ["shared_access_key_enabled"]),
		{"shared_access_key_enabled": true}, ["create"],
	)
	fixtures.rules_for(doc, []) == set()
}

# Plans: resources being deleted aren't checked.
test_plan_delete_is_not_checked if {
	doc := fixtures.plan_change("azurerm_storage_account", "this", null, {}, ["delete"])
	fixtures.rules_for(doc, []) == set()
}

test_plan_replace_is_checked if {
	doc := fixtures.plan_change(
		"azurerm_storage_account", "this",
		account({"shared_access_key_enabled": true}), {}, ["delete", "create"],
	)
	fixtures.rules_for(doc, []) == {"storage-shared-key-off"}
}
