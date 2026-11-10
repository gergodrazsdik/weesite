# Rules for every Azure resource: tags and locations. Settings come from
# policy/data/policy.yaml (`data.policy`).
package weesite.rules.general

import data.weesite.lib

# Taggable resource types, for the HCL tier, where a missing tags argument
# can't tell an untaggable type from a forgotten tag. A plan has the tags
# attribute (null when unset) on every taggable resource. Add types here as
# the infrastructure uses them.
taggable_types := {
	"azurerm_application_insights",
	"azurerm_container_app",
	"azurerm_container_app_environment",
	"azurerm_container_app_environment_managed_certificate",
	"azurerm_container_app_job",
	"azurerm_dns_a_record",
	"azurerm_dns_cname_record",
	"azurerm_dns_txt_record",
	"azurerm_dns_zone",
	"azurerm_key_vault",
	"azurerm_key_vault_key",
	"azurerm_log_analytics_workspace",
	"azurerm_monitor_action_group",
	"azurerm_resource_group",
	"azurerm_storage_account",
	"azurerm_user_assigned_identity",
}

taggable(r) if r.type in taggable_types

taggable(r) if {
	startswith(r.type, "azurerm_")
	"tags" in object.keys(r.values)
}

# Every taggable resource carries the required tag keys.
violation contains lib.violation("required-tags", r, msg) if {
	some r in lib.resources
	taggable(r)
	tags := lib.value(r, "tags")
	present := {k | some k, _ in tags_or_empty(tags)}
	missing := {k | some k in data.policy.required_tags; not k in present}
	count(missing) > 0
	msg := sprintf("missing tags: %v", [concat(", ", sort(missing))])
}

tags_or_empty(t) := t if is_object(t)

tags_or_empty(t) := {} if not is_object(t)

# Resources live in the allowed locations only. "global" isn't a region:
# Azure gives it to resources that live in none, such as action groups.
violation contains lib.violation("allowed-locations", r, msg) if {
	some r in lib.resources
	startswith(r.type, "azurerm_")
	location := lib.value(r, "location")
	is_string(location)
	lib.canonical_location(location) != "global"
	allowed := {lib.canonical_location(l) | some l in data.policy.allowed_locations}
	not lib.canonical_location(location) in allowed
	msg := sprintf("location %v is not allowed (allowed: %v)", [location, concat(", ", sort(allowed))])
}
