# Shared helpers for the Weesite policy baseline.
#
# The rules run on two inputs:
# - the HCL of a .tf file, parsed by `conftest test --parser hcl2`;
# - the JSON of a saved plan (`tofu show -json <plan>`).
# `resources` turns both into one shape, so every rule is written once.
package weesite.lib

# Every rule ID. A waiver that names another ID fails the check.
rule_ids := {
	"allowed-locations",
	"container-app-https-only",
	"keyvault-purge-protection",
	"keyvault-rbac",
	"managed-identity",
	"no-stored-secrets",
	"required-tags",
	"storage-https-only",
	"storage-min-tls",
	"storage-no-public-blob",
	"storage-shared-key-off",
}

# Managed resources, normalised to
# {"source", "type", "name", "address", "values", "unknown"}.
# From a plan: everything that isn't being deleted, with its planned values.
resources contains r if {
	some rc in input.resource_changes
	rc.mode == "managed"
	rc.change.actions != ["delete"]
	r := {
		"source": "plan",
		"type": rc.type,
		"name": rc.name,
		"address": rc.address,
		"values": rc.change.after,
		"unknown": object.get(rc.change, "after_unknown", {}),
	}
}

# From HCL: every resource block as written.
resources contains r if {
	some type, named in input.resource
	some name, bodies in named
	some body in bodies
	r := {
		"source": "hcl",
		"type": type,
		"name": name,
		"address": sprintf("%s.%s", [type, name]),
		"values": body,
		"unknown": {},
	}
}

# Resources of one type.
of_type(type) := {r | some r in resources; r.type == type}

# The value of attribute k: null when it isn't set, undefined when it isn't
# known yet (an expression in HCL, a value computed during apply in a plan).
# Rules that use it only judge what they can see; the plan tier sees the rest.
value(r, k) := object.get(r.values, k, null) if not unknown(r, k)

unknown(r, k) if r.unknown[k] == true

unknown(r, k) if {
	r.source == "hcl"
	is_string(r.values[k])
	contains(r.values[k], "${")
}

# The nested blocks named k ([] when there are none); undefined when HCL
# generates them with a `dynamic` block.
blocks(r, k) := object.get(r.values, k, []) if not dynamic(r, k)

dynamic(r, k) if {
	r.source == "hcl"
	some d in object.get(r.values, "dynamic", [])
	d[k]
}

# Azure location names in canonical form: "West Europe" -> "westeurope".
canonical_location(s) := lower(replace(s, " ", ""))

# Inline waivers, collected from `# policy:waive <rule> <reason>` comments
# by scripts/policy-waivers.sh and passed in with `--data`.
default waivers := []

waivers := data.waivers

# The waivers that cover a violation: same rule, same resource type and name
# (in any module of the root being checked).
waivers_for(v) := {w |
	some w in waivers
	w.resource == sprintf("%s.%s", [v.type, v.name])
	w.rule == v.rule
}

# A violation found by a rule.
violation(rule, r, msg) := {
	"rule": rule,
	"type": r.type,
	"name": r.name,
	"address": r.address,
	"msg": msg,
}
