# Role assignments (T-0122): no broad roles, no broad scopes. Values that are
# expressions in HCL (a resource's id) are judged in the plan, where they're known.
package weesite.rules.roles

import data.weesite.lib

# Roles that can grant every other role or do everything.
broad_roles := {"Owner", "User Access Administrator"}

# Never Owner or User Access Administrator.
violation contains lib.violation("role-not-broad", r, sprintf("%s is too broad: use a narrower role", [name])) if {
	some r in lib.of_type("azurerm_role_assignment")
	name := lib.value(r, "role_definition_name")
	name in broad_roles
}

# Role Based Access Control Administrator only with a condition that limits
# what it may assign.
violation contains lib.violation("role-admin-conditioned", r, "Role Based Access Control Administrator needs a condition") if {
	some r in lib.of_type("azurerm_role_assignment")
	lib.value(r, "role_definition_name") == "Role Based Access Control Administrator"
	not lib.unknown(r, "condition")
	object.get(r.values, "condition", null) in {null, ""}
}

# Never at a subscription or management group: a resource group or narrower.
violation contains lib.violation("role-scope-narrow", r, sprintf("scope %s is a whole subscription or management group", [scope])) if {
	some r in lib.of_type("azurerm_role_assignment")
	scope := lib.value(r, "scope")
	is_string(scope)
	broad_scope(scope)
}

broad_scope(scope) if regex.match(`^/subscriptions/[^/]+/?$`, scope)

broad_scope(scope) if startswith(lower(scope), "/providers/microsoft.management/managementgroups/")
