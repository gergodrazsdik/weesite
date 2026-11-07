# Entry point for conftest (namespace `main`): collects the violations of
# every rule package under weesite.rules, applies the inline waivers and
# reports what's left.
package main

import data.weesite.lib

violations contains v if {
	some pkg in data.weesite.rules
	some v in pkg.violation
}

deny contains sprintf("%s: %s: %s", [v.rule, v.address, v.msg]) if {
	some v in violations
	count(lib.waivers_for(v)) == 0
}

# A waived violation stays visible as a warning, with its reason.
warn contains sprintf("%s: %s: waived at %s: %s", [v.rule, v.address, w.at, w.reason]) if {
	some v in violations
	some w in lib.waivers_for(v)
}

# A waiver must name a real rule.
deny contains sprintf("waiver at %s names unknown rule %q", [w.at, w.rule]) if {
	some w in lib.waivers
	not w.rule in lib.rule_ids
}

# Every rule reports an ID listed in lib.rule_ids, so it can be waived.
deny contains sprintf("rule ID %q is missing from lib.rule_ids", [v.rule]) if {
	some v in violations
	not v.rule in lib.rule_ids
}
