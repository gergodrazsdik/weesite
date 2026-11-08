---
title: "Moving from Bicep to OpenTofu"
slug: moving-from-bicep-to-opentofu
date: 2026-10-12
author: example-author
lang: en
tags: [azure, opentofu]
summary: "What changed when the same resources moved from Bicep to OpenTofu."
---

Bicep is pleasant for Azure alone. OpenTofu won here for state encryption, a policy step on plans, and providers beyond Azure, such as GitHub.

## The same storage account

```bicep
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${uniqueString(resourceGroup().id)}'
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}
```

```hcl
resource "azurerm_storage_account" "data" {
  name                            = "st${local.suffix}"
  location                        = var.location
  resource_group_name             = azurerm_resource_group.main.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
}
```

## What I miss

Bicep's what-if output is easy to read. OpenTofu's plan JSON is easier to check by machine, which is what [the policies](/blog/a-small-rego-baseline-for-infrastructure) need.
