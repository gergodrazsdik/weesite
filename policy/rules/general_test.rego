package weesite.tests.general

import data.weesite.fixtures

test_compliant_resource_group_passes if {
	fixtures.rules_both("azurerm_resource_group", fixtures.base) == {set()}
}

test_missing_tag_fails if {
	tags := object.remove(fixtures.tags, ["managed-by"])
	fixtures.rules_both("azurerm_resource_group", object.union(object.remove(fixtures.base, ["tags"]), {"tags": tags})) == {{"required-tags"}}
}

test_missing_tags_argument_fails if {
	fixtures.rules_both("azurerm_resource_group", object.remove(fixtures.base, ["tags"])) == {{"required-tags"}}
}

test_tags_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_resource_group", "this", object.union(fixtures.base, {"tags": "${local.tags}"}))
	fixtures.rules_for(doc, []) == set()
}

# A plan has tags (null when unset) on every taggable type, listed or not.
test_plan_unlisted_taggable_type_fails if {
	doc := fixtures.plan("azurerm_new_thing", "this", {"location": "westeurope", "tags": null})
	fixtures.rules_for(doc, []) == {"required-tags"}
}

test_untaggable_type_passes if {
	fixtures.rules_both("azurerm_role_assignment", {"scope": "/subscriptions/x"}) == {set()}
}

test_location_outside_list_fails if {
	fixtures.rules_both("azurerm_resource_group", object.union(fixtures.base, {"location": "eastus"})) == {{"allowed-locations"}}
}

test_location_display_name_passes if {
	fixtures.rules_both("azurerm_resource_group", object.union(fixtures.base, {"location": "West Europe"})) == {set()}
}

test_location_expression_is_not_judged if {
	doc := fixtures.hcl("azurerm_resource_group", "this", object.union(fixtures.base, {"location": "${var.location}"}))
	fixtures.rules_for(doc, []) == set()
}
