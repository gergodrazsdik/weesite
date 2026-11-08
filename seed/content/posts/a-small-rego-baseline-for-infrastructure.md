---
title: "A small Rego baseline for infrastructure"
slug: a-small-rego-baseline-for-infrastructure
date: 2026-03-28
author: example-author
lang: en
tags: [rego, opentofu, security]
summary: "Ten rules, each with a test, and inline waivers with a reason."
---

A policy baseline doesn't need to be big. Ten rules catch most mistakes: public blobs, old TLS, missing tags.

## A rule

```rego
package weesite.storage

import rego.v1

deny contains msg if {
    some change in input.resource_changes
    change.type == "azurerm_storage_account"
    change.change.after.min_tls_version != "TLS1_2"
    msg := sprintf("%s: minimum TLS version must be 1.2", [change.address])
}
```

## Its test

```rego
package weesite.storage_test

import rego.v1

import data.weesite.storage

test_old_tls_is_denied if {
    count(storage.deny) == 1 with input as {"resource_changes": [{
        "address": "azurerm_storage_account.data",
        "type": "azurerm_storage_account",
        "change": {"after": {"min_tls_version": "TLS1_0"}},
    }]}
}
```

## Waivers

Some rules have good exceptions. A waiver sits in a comment above the resource and must give a reason, so the exception is reviewed with the code.
