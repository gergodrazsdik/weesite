# Test helpers: build both input shapes from one resource, so every test
# can check the HCL tier and the plan tier with the same values.
package weesite.fixtures

# conftest's hcl2 parser: resource.<type>.<name> is a list of one body.
hcl(type, name, body) := {"resource": {type: {name: [body]}}}

# `tofu show -json`: a resource change with the planned values. Unknown
# values are left out of `after` and marked in `after_unknown`.
plan(type, name, after) := plan_change(type, name, after, {}, ["create"])

plan_change(type, name, after, after_unknown, actions) := {"resource_changes": [{
	"address": sprintf("%s.%s", [type, name]),
	"mode": "managed",
	"type": type,
	"name": name,
	"change": {"actions": actions, "after": after, "after_unknown": after_unknown},
}]}

# Tags and location that pass the general rules with the test settings.
tags := {"workload": "weesite", "environment": "test", "managed-by": "opentofu"}

settings := {
	"required_tags": ["workload", "environment", "managed-by"],
	"allowed_locations": ["westeurope"],
}

base := {"location": "westeurope", "tags": tags}

# The rule IDs that fire for an input, with the test settings and waivers.
rules_for(doc, waivers) := {v.rule |
	some v in data.main.violations with input as doc
		with data.policy as settings
		with data.waivers as waivers
}

denied(doc, waivers) := result if {
	result := data.main.deny with input as doc
		with data.policy as settings
		with data.waivers as waivers
}

warned(doc, waivers) := result if {
	result := data.main.warn with input as doc
		with data.policy as settings
		with data.waivers as waivers
}

# Both shapes of one resource.
both(type, body) := [hcl(type, "this", body), plan(type, "this", body)]

# The rule IDs for each shape of one resource, without waivers. A test
# compares it with {expected}, which holds only when both shapes agree.
rules_both(type, body) := {rules_for(doc, []) | some doc in both(type, body)}
