terraform {
  required_version = ">= 1.13.0"

  required_providers {
    # One resource: binding the managed certificates to the custom domains, which azurerm can't
    # do yet (domain.tf).
    azapi = {
      source  = "azure/azapi"
      version = "~> 2.13.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10.0"
    }
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.11.0"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.14.2"
    }
  }
}
