# Role assignments (T-0122): no broad roles, no broad scopes, and only through
# azurerm_role_assignment, where these rules can see them. Values that are
# expressions in HCL (a resource's id) are judged in the plan, where they're
# known: infra-plan for infra/app, just bootstrap-plan for infra/bootstrap.
package weesite.rules.roles

import data.weesite.lib

# Roles that can grant every other role or do everything, by name (lower case)
# and by the built-in definition's ID, which is the same in every tenant.
broad_roles := {"owner", "user access administrator"}

broad_role_ids := {
	"8e3af657-a8ff-443c-a75c-2fe8c4bcb635", # Owner
	"18d7d88d-d35e-4fb5-a5c3-7773c20a72d9", # User Access Administrator
}

rbac_admin := "role based access control administrator"

rbac_admin_id := "f58310d9-a9f6-439a-9e8d-f62e7b41a168"

# The roles an assignment names, lower case: its name, the GUID at the end of
# its definition ID, or both (a plan knows both for an assignment that exists).
roles(r) := {lower(name) | name := lib.value(r, "role_definition_name"); is_string(name)} | {lower(guid) |
	id := lib.value(r, "role_definition_id")
	is_string(id)
	parts := split(trim_right(id, "/"), "/")
	guid := parts[count(parts) - 1]
}

# Never Owner or User Access Administrator.
violation contains lib.violation("role-not-broad", r, sprintf("%s is too broad: use a narrower role", [name])) if {
	some r in lib.of_type("azurerm_role_assignment")
	some name in roles(r)
	broad(name)
}

broad(name) if name in broad_roles

broad(name) if name in broad_role_ids

# Role Based Access Control Administrator only with a condition that limits
# what it may assign.
violation contains lib.violation("role-admin-conditioned", r, "Role Based Access Control Administrator needs a condition") if {
	some r in lib.of_type("azurerm_role_assignment")
	some name in roles(r)
	name in {rbac_admin, rbac_admin_id}
	not lib.unknown(r, "condition")
	condition := object.get(r.values, "condition", null)
	not conditioned(condition)
}

conditioned(condition) if {
	is_string(condition)
	trim_space(condition) != ""
}

# Never at a subscription, a management group or the tenant root: a resource
# group or narrower.
violation contains lib.violation("role-scope-narrow", r, sprintf("scope %s is a whole subscription, management group or tenant", [scope])) if {
	some r in lib.of_type("azurerm_role_assignment")
	scope := lib.value(r, "scope")
	is_string(scope)
	broad_scope(lower(scope))
}

# In HCL, a subscription's ID built from a variable or a subscription data
# source is broad too, even before the plan knows it.
violation contains lib.violation("role-scope-narrow", r, sprintf("scope %s is a whole subscription", [scope])) if {
	some r in lib.of_type("azurerm_role_assignment")
	r.source == "hcl"
	scope := object.get(r.values, "scope", null)
	is_string(scope)
	broad_scope_expression(lower(scope))
}

broad_scope(scope) if regex.match(`^/subscriptions/[^/]+/?$`, scope)

broad_scope(scope) if startswith(scope, "/providers/microsoft.management/managementgroups/")

broad_scope(scope) if scope in {"/", ""}

broad_scope_expression(scope) if regex.match(`^/subscriptions/\$\{[^}]+\}/?$`, scope)

broad_scope_expression(scope) if regex.match(`^\$\{data\.azurerm_subscription\.[^}]+\.id\}$`, scope)

# Roles are assigned only with azurerm_role_assignment: PIM assignments and
# azapi role assignments would pass the rules above unseen.
violation contains lib.violation("role-assignment-plain", r, sprintf("%s assigns a role where the policy can't check it: use azurerm_role_assignment", [r.type])) if {
	some r in lib.resources
	r.type in {"azurerm_pim_active_role_assignment", "azurerm_pim_eligible_role_assignment"}
}

# The same for azapi, which creates (azapi_resource) or changes (azapi_update_resource) any ARM
# resource: role assignments and role definitions go through the azurerm resources (F-0251).
violation contains lib.violation("role-assignment-plain", r, "an azapi role assignment or definition can't be checked: use azurerm_role_assignment or azurerm_role_definition") if {
	some r in lib.resources
	r.type in {"azapi_resource", "azapi_update_resource"}
	type := lib.value(r, "type")
	is_string(type)
	authorization_type(lower(type))
}

authorization_type(type) if startswith(type, "microsoft.authorization/roleassignments")

authorization_type(type) if startswith(type, "microsoft.authorization/roledefinitions")

# A custom role mustn't do everything or assign roles itself: no `*`, no wildcard over every
# provider's writes or deletes, no wildcard or write in Microsoft.Authorization; and no wildcard
# data action, which reads or writes every blob, secret or message (F-0251).
violation contains lib.violation("role-definition-narrow", r, sprintf("custom role allows %s", [action])) if {
	some r in lib.of_type("azurerm_role_definition")
	some permissions in lib.blocks(r, "permissions")
	some action in object.get(permissions, "actions", [])
	is_string(action)
	broad_action(lower(action))
}

violation contains lib.violation("role-definition-narrow", r, sprintf("custom role allows the data action %s", [action])) if {
	some r in lib.of_type("azurerm_role_definition")
	some permissions in lib.blocks(r, "permissions")
	some action in object.get(permissions, "data_actions", [])
	is_string(action)
	contains(action, "*")
}

broad_action("*")

broad_action(action) if {
	startswith(action, "*/")
	action != "*/read"
}

broad_action(action) if {
	startswith(action, "microsoft.authorization/")
	contains(action, "*")
}

broad_action(action) if action in {"microsoft.authorization/roleassignments/write", "microsoft.authorization/roledefinitions/write"}

# Key Vault Secrets User reads secret values: on one vault or one secret, never on a resource
# group or wider, where it reaches every secret of every vault in it, those stored by hand
# included (T-0177).
# By name, and by the built-in definition's ID, the same in every tenant.
vault_value_reader := {
	"4633458b-17de-408a-b874-0445c86b69e6",
	"key vault secrets user",
}

violation contains lib.violation("keyvault-secrets-user-narrow", r, sprintf("Key Vault Secrets User at %s: give it on a vault or a secret", [scope])) if {
	some r in lib.of_type("azurerm_role_assignment")
	some name in roles(r)
	name in vault_value_reader
	scope := lib.value(r, "scope")
	is_string(scope)
	not contains(lower(scope), "/providers/microsoft.keyvault/vaults/")
}

# Microsoft Graph application permissions (azuread_app_role_assignment) are tenant-wide, more
# than any resource group role: only the two the bootstrap grants (F-0251). In HCL, the role is
# read from the expression that names it (app_role_ids["Application.Read.All"]); in a plan, by
# its ID, which is the same in every tenant.
graph_roles := {"application.readwrite.ownedby", "application.read.all"}

graph_role_ids := {
	"18a4783c-866b-4cc7-a460-3d5e5662c884", # Application.ReadWrite.OwnedBy
	"9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30", # Application.Read.All
}

violation contains lib.violation("graph-role-allowed", r, sprintf("app role %v isn't one of the Graph permissions the baseline allows (Application.ReadWrite.OwnedBy, Application.Read.All)", [object.get(r.values, "app_role_id", null)])) if {
	some r in lib.of_type("azuread_app_role_assignment")
	not graph_role_allowed(r)
}

graph_role_allowed(r) if lower(lib.value(r, "app_role_id")) in graph_role_ids

graph_role_allowed(r) if {
	r.source == "hcl"
	names := regex.find_all_string_submatch_n(`app_role_ids\["([^"]+)"\]`, r.values.app_role_id, -1)
	count(names) == 1
	lower(names[0][1]) in graph_roles
}

# A plan that doesn't know the ID yet can't judge it.
graph_role_allowed(r) if {
	r.source == "plan"
	lib.unknown(r, "app_role_id")
}
