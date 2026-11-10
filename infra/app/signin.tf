# Sign-in (ADR 0004): the environment's Entra app registration for "Sign in with Microsoft"
# (personal and work accounts), its client secret, and both written to the app's Key Vault,
# which the web app reads at startup. Google's client is created by hand and stored with
# `just google-signin <env>` (docs/deploy.md).

# The apply identity owns the app: with Graph Application.ReadWrite.OwnedBy it can change only
# apps it owns. It's looked up by name, not as "whoever runs OpenTofu"
# (azuread_client_config): that would be the plan identity, which computes the plan the apply
# then carries out (F-0029).
data "azuread_service_principal" "apply" {
  display_name = "id-${local.workload}-apply-${var.environment}"
}

locals {
  # Microsoft Graph and its delegated sign-in scopes (well-known IDs, the same in every tenant).
  graph_app_id = "00000003-0000-0000-c000-000000000000"
  graph_scopes = {
    openid  = "37f7f235-527c-4136-accd-4a02d197296e"
    email   = "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0"
    profile = "14dad69e-099b-42c9-810b-d002981feec1"
  }

  # The web app's hostname is "{app}.{environment's default domain}". It's built here rather than
  # read from the Container App, which depends on the secret below (a cycle otherwise).
  web_hostname = "ca-${local.workload}-web.${azurerm_container_app_environment.main.default_domain}"
  signin_hosts = compact([local.web_hostname, var.domain])

  # The site's address for the consent screen's links (T-0072): its own domain, or the Container
  # Apps hostname. The app redirects /legal/privacy and /legal/terms to the pages Site settings
  # chose, so these don't change when another page is chosen.
  site_url = "https://${var.domain == "" ? local.web_hostname : var.domain}"

  # test also signs in from local development (`just aspire`, Weesite.Web's launch settings).
  microsoft_redirect_uris = concat(
    [for host in local.signin_hosts : "https://${host}/signin-microsoft"],
    local.prod ? [] : ["http://localhost:5080/signin-microsoft"],
  )
}

resource "azuread_application" "signin" {
  display_name     = "${local.workload}-${var.environment}"
  sign_in_audience = "AzureADandPersonalMicrosoftAccount"

  # Shown on Microsoft's sign-in and consent pages: Weesite's logo, 215 px (under 100 KB), when
  # the app is created. A site's own logo (Site settings, T-0116) is uploaded in the portal once,
  # and later applies leave it (lifecycle below).
  logo_image = filebase64("${path.module}/../../src/Weesite.Web/wwwroot/icons/logo-215.png")
  owners     = [data.azuread_service_principal.apply.object_id]

  # Linked from Microsoft's consent page (T-0072). The publisher domain can't be set here (Graph
  # treats it as read-only); docs/deploy.md has the one step in the portal.
  privacy_statement_url = "${local.site_url}/legal/privacy"
  terms_of_service_url  = "${local.site_url}/legal/terms"

  # Personal Microsoft accounts need v2 tokens.
  api {
    requested_access_token_version = 2
  }

  web {
    homepage_url  = "${local.site_url}/"
    redirect_uris = local.microsoft_redirect_uris

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  lifecycle {
    ignore_changes = [logo_image]
  }

  required_resource_access {
    resource_app_id = local.graph_app_id

    dynamic "resource_access" {
      for_each = local.graph_scopes

      content {
        id   = resource_access.value
        type = "Scope"
      }
    }
  }
}

# Rotates every 180 days and lasts a year. A rotation replaces the password: the same apply
# writes the new Key Vault version, rolls the Container App to a new revision (the version is
# one of its settings) and only then deletes the old password.
resource "time_rotating" "signin" {
  rotation_days = 180
}

resource "azuread_application_password" "signin" {
  application_id      = azuread_application.signin.id
  display_name        = "${local.workload}-${var.environment}"
  end_date            = timeadd(time_rotating.signin.id, "8760h")
  rotate_when_changed = { rotation = time_rotating.signin.id }

  lifecycle {
    create_before_destroy = true
  }
}

locals {
  signin_secrets = {
    "Authentication--Microsoft--ClientId"     = azuread_application.signin.client_id
    "Authentication--Microsoft--ClientSecret" = azuread_application_password.signin.value
  }
}

# No prevent_destroy: these values come from the app registration above and are written again
# by the next apply.
# tflint-ignore: azurerm_resources_missing_prevent_destroy
resource "azurerm_key_vault_secret" "signin" {
  for_each = nonsensitive(toset(keys(local.signin_secrets)))

  name            = each.key
  value           = local.signin_secrets[each.key]
  key_vault_id    = azurerm_key_vault.app.id
  content_type    = "text/plain"
  expiration_date = azuread_application_password.signin.end_date
  tags            = local.tags
}
