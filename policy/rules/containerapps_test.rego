package weesite.tests.containerapps

import data.weesite.fixtures

good_app := object.union(fixtures.base, {
	"identity": [{"type": "UserAssigned"}],
	"ingress": [{"external_enabled": true, "target_port": 8080}],
	"secret": [{"name": "auth-client-secret", "key_vault_secret_id": "${azurerm_key_vault_secret.auth.id}"}],
})

app(overrides) := object.union(good_app, overrides)

test_compliant_app_passes if {
	fixtures.rules_both("azurerm_container_app", good_app) == {set()}
}

test_insecure_ingress_fails if {
	fixtures.rules_both("azurerm_container_app", app({"ingress": [{"allow_insecure_connections": true}]})) == {{"container-app-https-only"}}
}

test_missing_identity_fails if {
	every type in {"azurerm_container_app", "azurerm_container_app_job"} {
		fixtures.rules_both(type, object.remove(good_app, ["identity"])) == {{"managed-identity"}}
	}
}

test_dynamic_identity_is_not_judged if {
	app_ := object.union(object.remove(good_app, ["identity"]), {"dynamic": [{"identity": [{"for_each": "${var.ids}"}]}]})
	doc := fixtures.hcl("azurerm_container_app", "this", app_)
	fixtures.rules_for(doc, []) == set()
}

test_registry_password_fails if {
	fixtures.rules_both("azurerm_container_app", app({"registry": [{"server": "registry.example", "password_secret_name": "pw"}]})) == {{"managed-identity"}}
}

test_registry_identity_passes if {
	fixtures.rules_both("azurerm_container_app", app({"registry": [{"server": "registry.example", "identity": "${azurerm_user_assigned_identity.app.id}"}]})) == {set()}
}

test_stored_secret_fails if {
	every type in {"azurerm_container_app", "azurerm_container_app_job"} {
		fixtures.rules_both(type, app({"secret": [{"name": "s", "value": "${var.secret}"}]})) == {{"no-stored-secrets"}}
	}
}
