terraform {
  required_version = ">= 1.13.0"

  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10.0"
    }
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.12.0"
    }
    github = {
      source  = "integrations/github"
      version = "~> 6.13.0"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.14.2"
    }
  }
}
