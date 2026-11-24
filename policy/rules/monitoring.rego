# Application Insights. Defaults are azurerm 5.x's.
package weesite.rules.monitoring

import data.weesite.lib

# Telemetry only through Entra ID (the default also takes it with the connection string alone,
# which then works as a write credential that never expires: T-0177).
violation contains lib.violation("appinsights-entra-only", r, "local_authentication_enabled must be false") if {
	some r in lib.of_type("azurerm_application_insights")
	lib.value(r, "local_authentication_enabled") != false
}
