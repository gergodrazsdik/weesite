# Sign-in (ADR 0004): the environment's Entra app registration for "Sign in with Microsoft"
# (personal and work accounts), its client secret, and both written to the app's Key Vault,
# which the web app reads at startup. Google's client is created by hand and stored with
# `just google-signin <env>` (docs/deploy.md).

data "azuread_client_config" "current" {}

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

  # test also signs in from local development (`just aspire`, Weesite.Web's launch settings).
  microsoft_redirect_uris = concat(
    [for host in local.signin_hosts : "https://${host}/signin-microsoft"],
    local.prod ? [] : ["http://localhost:5080/signin-microsoft"],
  )
}

resource "azuread_application" "signin" {
  display_name     = "${local.workload}-${var.environment}"
  sign_in_audience = "AzureADandPersonalMicrosoftAccount"
  owners           = [data.azuread_client_config.current.object_id]

  # Personal Microsoft accounts need v2 tokens.
  api {
    requested_access_token_version = 2
  }

  web {
    redirect_uris = local.microsoft_redirect_uris

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
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
