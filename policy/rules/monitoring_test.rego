package weesite.tests.monitoring

import data.weesite.fixtures

good_insights := object.union(fixtures.base, {"local_authentication_enabled": false})

test_entra_only_passes if {
	fixtures.rules_both("azurerm_application_insights", good_insights) == {set()}
}

test_local_authentication_default_fails if {
	fixtures.rules_both("azurerm_application_insights", object.remove(good_insights, ["local_authentication_enabled"])) == {{"appinsights-entra-only"}}
}

test_local_authentication_on_fails if {
	fixtures.rules_both("azurerm_application_insights", object.union(good_insights, {"local_authentication_enabled": true})) == {{"appinsights-entra-only"}}
}

test_local_authentication_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_application_insights", "this", object.union(good_insights, {"local_authentication_enabled": "${var.local}"}))
	fixtures.rules_for(doc, []) == set()
}
