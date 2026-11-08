---
title: "Encrypting OpenTofu state with Key Vault"
slug: encrypting-opentofu-state-with-key-vault
date: 2026-04-15
updated: 2026-05-01
author: example-author
lang: en
tags: [opentofu, azure, security]
summary: "State holds secrets, so OpenTofu encrypts it before it reaches the storage account."
---

OpenTofu can encrypt state and plans itself. With Azure Key Vault as the key provider, the storage account only ever sees ciphertext.

```hcl
terraform {
  encryption {
    key_provider "azure_vault" "state" {
      vault_uri = var.vault_uri
      key_name  = "state"
      key_length = 32
    }

    method "aes_gcm" "state" {
      keys = key_provider.azure_vault.state
    }

    state {
      method   = method.aes_gcm.state
      enforced = true
    }
  }
}
```

## What it protects against

- a storage account key leaking;
- someone with read access to the container but not to the vault;
- an old state file in a backup.

## What it doesn't

Anyone who can use the key can read the state. Keep the key's access as narrow as the state's.
