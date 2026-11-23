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

# Second review round (T-0122): the ways round the rules that it found.
owner_id := "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635"

test_owner_by_definition_id_fails if {
	body := object.union(object.remove(good, ["role_definition_name"]), {"role_definition_id": owner_id})
	fixtures.rules_both("azurerm_role_assignment", body) == {{"role-not-broad"}}
}

test_owner_in_lower_case_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"role_definition_name": "owner"})) == {{"role-not-broad"}}
}

test_a_plan_knowing_both_name_and_id_is_judged_once if {
	body := object.union(good, {"role_definition_name": "Owner", "role_definition_id": owner_id})
	fixtures.rules_for(fixtures.plan("azurerm_role_assignment", "this", body), []) == {"role-not-broad"}
}

test_rbac_admin_by_id_without_condition_fails if {
	body := object.union(object.remove(good, ["role_definition_name"]), {"role_definition_id": "/providers/Microsoft.Authorization/roleDefinitions/f58310d9-a9f6-439a-9e8d-f62e7b41a168"})
	fixtures.rules_both("azurerm_role_assignment", body) == {{"role-admin-conditioned"}}
}

test_rbac_admin_with_a_blank_condition_fails if {
	body := object.union(good, {"role_definition_name": "Role Based Access Control Administrator", "condition": "  "})
	fixtures.rules_both("azurerm_role_assignment", body) == {{"role-admin-conditioned"}}
}

test_subscription_scope_in_other_case_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"scope": "/Subscriptions/00000000-0000-0000-0000-000000000000"})) == {{"role-scope-narrow"}}
}

test_tenant_root_scope_fails if {
	fixtures.rules_both("azurerm_role_assignment", object.union(good, {"scope": "/"})) == {{"role-scope-narrow"}}
}

test_subscription_scope_from_a_variable_fails_in_hcl if {
	doc := fixtures.hcl("azurerm_role_assignment", "this", object.union(good, {"scope": "/subscriptions/${var.subscription_id}"}))
	fixtures.rules_for(doc, []) == {"role-scope-narrow"}
}

test_subscription_data_source_scope_fails_in_hcl if {
	doc := fixtures.hcl("azurerm_role_assignment", "this", object.union(good, {"scope": "${data.azurerm_subscription.current.id}"}))
	fixtures.rules_for(doc, []) == {"role-scope-narrow"}
}

test_resource_group_scope_from_a_variable_passes_in_hcl if {
	doc := fixtures.hcl("azurerm_role_assignment", "this", object.union(good, {"scope": "/subscriptions/${var.subscription_id}/resourceGroups/rg"}))
	fixtures.rules_for(doc, []) == set()
}

test_pim_assignment_fails if {
	"role-assignment-plain" in fixtures.rules_for(fixtures.plan("azurerm_pim_active_role_assignment", "this", {"scope": group, "role_definition_id": owner_id, "principal_id": "p"}), [])
}

test_azapi_role_assignment_fails if {
	body := {"type": "Microsoft.Authorization/roleAssignments@2022-04-01", "parent_id": group, "name": "x"}
	"role-assignment-plain" in fixtures.rules_for(fixtures.plan("azapi_resource", "this", body), [])
}

test_other_azapi_resource_passes if {
	body := {"type": "Microsoft.App/containerApps@2024-03-01", "parent_id": group, "name": "x"}
	not "role-assignment-plain" in fixtures.rules_for(fixtures.plan("azapi_resource", "this", body), [])
}

test_custom_role_that_assigns_roles_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["Microsoft.Authorization/roleAssignments/write"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_custom_role_that_does_everything_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["*"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_narrow_custom_role_passes if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["Microsoft.App/containerApps/listSecrets/action"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {set()}
}

# Full review (F-0251): custom roles' data actions and wildcards, azapi updates, Graph permissions.
test_custom_role_with_a_wildcard_data_action_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": [], "data_actions": ["Microsoft.KeyVault/vaults/secrets/*"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_custom_role_with_every_data_action_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"data_actions": ["*"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_custom_role_with_one_data_action_passes if {
	body := {"name": "r", "scope": group, "permissions": [{"data_actions": ["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/deleteBlobVersion/action"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {set()}
}

test_custom_role_that_writes_everything_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["*/write"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_custom_role_that_reads_everything_passes if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["*/read"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {set()}
}

test_custom_role_with_an_authorization_wildcard_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["Microsoft.Authorization/*/Write"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_custom_role_that_writes_role_definitions_fails if {
	body := {"name": "r", "scope": group, "permissions": [{"actions": ["Microsoft.Authorization/roleDefinitions/write"]}]}
	fixtures.rules_both("azurerm_role_definition", body) == {{"role-definition-narrow"}}
}

test_azapi_update_of_a_role_assignment_fails if {
	body := {"type": "Microsoft.Authorization/roleAssignments@2022-04-01", "resource_id": "x"}
	"role-assignment-plain" in fixtures.rules_for(fixtures.plan("azapi_update_resource", "this", body), [])
}

test_azapi_role_definition_fails if {
	body := {"type": "Microsoft.Authorization/roleDefinitions@2022-04-01", "parent_id": group, "name": "x"}
	"role-assignment-plain" in fixtures.rules_for(fixtures.plan("azapi_resource", "this", body), [])
}

test_other_azapi_update_passes if {
	body := {"type": "Microsoft.OperationalInsights/workspaces@2023-09-01", "resource_id": "x"}
	not "role-assignment-plain" in fixtures.rules_for(fixtures.plan("azapi_update_resource", "this", body), [])
}

graph_grant(role_id) := {"app_role_id": role_id, "principal_object_id": "p", "resource_object_id": "g"}

test_graph_permission_by_name_in_hcl_passes if {
	doc := fixtures.hcl("azuread_app_role_assignment", "this", graph_grant(`${data.azuread_service_principal.graph.app_role_ids["Application.ReadWrite.OwnedBy"]}`))
	fixtures.rules_for(doc, []) == set()
}

test_another_graph_permission_by_name_in_hcl_fails if {
	doc := fixtures.hcl("azuread_app_role_assignment", "this", graph_grant(`${data.azuread_service_principal.graph.app_role_ids["Application.ReadWrite.All"]}`))
	fixtures.rules_for(doc, []) == {"graph-role-allowed"}
}

test_graph_permission_from_a_variable_fails_in_hcl if {
	doc := fixtures.hcl("azuread_app_role_assignment", "this", graph_grant("${var.role}"))
	fixtures.rules_for(doc, []) == {"graph-role-allowed"}
}

test_allowed_graph_permission_by_id_passes if {
	fixtures.rules_both("azuread_app_role_assignment", graph_grant("9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30")) == {set()}
}

test_other_graph_permission_by_id_fails if {
	# RoleManagement.ReadWrite.Directory
	fixtures.rules_both("azuread_app_role_assignment", graph_grant("9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8")) == {{"graph-role-allowed"}}
}

test_graph_permission_unknown_in_the_plan_is_not_judged if {
	doc := fixtures.plan_change("azuread_app_role_assignment", "this", {"principal_object_id": "p"}, {"app_role_id": true}, ["create"])
	fixtures.rules_for(doc, []) == set()
}
