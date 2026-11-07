package weesite.tests.main

import data.weesite.fixtures

public_account := object.union(fixtures.base, {
	"allow_nested_items_to_be_public": true,
	"shared_access_key_enabled": false,
})

waiver(rule, resource) := {
	"rule": rule,
	"resource": resource,
	"reason": "Test reason.",
	"at": "infra/x/main.tf:1",
}

test_violation_is_denied if {
	doc := fixtures.hcl("azurerm_storage_account", "web", public_account)
	fixtures.denied(doc, []) == {"storage-no-public-blob: azurerm_storage_account.web: allow_nested_items_to_be_public must not be true"}
}

test_waiver_turns_denial_into_warning if {
	waivers := [waiver("storage-no-public-blob", "azurerm_storage_account.web")]
	every doc in [
		fixtures.hcl("azurerm_storage_account", "web", public_account),
		fixtures.plan("azurerm_storage_account", "web", public_account),
	] {
		fixtures.denied(doc, waivers) == set()
		fixtures.warned(doc, waivers) == {"storage-no-public-blob: azurerm_storage_account.web: waived at infra/x/main.tf:1: Test reason."}
	}
}

# Plans: waivers match the resource's type and name inside a module too.
test_waiver_matches_module_resource if {
	doc := {"resource_changes": [{
		"address": "module.site.azurerm_storage_account.web",
		"mode": "managed",
		"type": "azurerm_storage_account",
		"name": "web",
		"change": {"actions": ["update"], "after": public_account, "after_unknown": {}},
	}]}
	fixtures.denied(doc, [waiver("storage-no-public-blob", "azurerm_storage_account.web")]) == set()
}

test_waiver_for_other_rule_does_not_apply if {
	doc := fixtures.hcl("azurerm_storage_account", "web", public_account)
	count(fixtures.denied(doc, [waiver("storage-shared-key-off", "azurerm_storage_account.web")])) == 1
}

test_waiver_for_other_resource_does_not_apply if {
	doc := fixtures.hcl("azurerm_storage_account", "web", public_account)
	count(fixtures.denied(doc, [waiver("storage-no-public-blob", "azurerm_storage_account.state")])) == 1
}

test_waiver_with_unknown_rule_is_denied if {
	doc := fixtures.hcl("azurerm_resource_group", "this", fixtures.base)
	fixtures.denied(doc, [waiver("no-such-rule", "azurerm_resource_group.this")]) == {`waiver at infra/x/main.tf:1 names unknown rule "no-such-rule"`}
}

# Resources of other providers aren't judged by the Azure rules.
test_other_providers_pass if {
	fixtures.rules_both("github_repository_environment", {"environment": "prod"}) == {set()}
}
