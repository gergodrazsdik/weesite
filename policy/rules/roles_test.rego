package weesite.tests.roles

import data.weesite.fixtures

group := "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-weesite-test"

good := {"scope": group, "role_definition_name": "Storage Blob Data Reader", "principal_id": "p"}

test_narrow_assignment_passes if {
	fixtures.rules_both("azurerm_role_assignment", good) == {set()}
}

test_owner_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"role_definition_name": "Owner"})) == {{"role-not-broad"}}
}

test_user_access_administrator_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"role_definition_name": "User Access Administrator"})) == {{"role-not-broad"}}
}

test_rbac_admin_without_condition_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"role_definition_name": "Role Based Access Control Administrator"})) == {{"role-admin-conditioned"}}
}

test_rbac_admin_with_condition_passes if {
	body := object.union(good, {"role_definition_name": "Role Based Access Control Administrator", "condition": "((!(ActionMatches{'x'})))", "condition_version": "2.0"})
	fixtures.rules_both("azurerm_role_assignment", body) == {set()}
}

test_subscription_scope_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"scope": "/subscriptions/00000000-0000-0000-0000-000000000000"})) == {{"role-scope-narrow"}}
}

test_management_group_scope_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"scope": "/providers/Microsoft.Management/managementGroups/root"})) == {{"role-scope-narrow"}}
}

test_scope_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_role_assignment", "this", object.union(good, {"scope": "${azurerm_resource_group.env.id}"}))
	fixtures.rules_for(doc, []) == set()
}
