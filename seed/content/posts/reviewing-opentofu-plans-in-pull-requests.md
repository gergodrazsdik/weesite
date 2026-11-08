---
title: "Reviewing OpenTofu plans in pull requests"
slug: reviewing-opentofu-plans-in-pull-requests
date: 2026-03-12
author: example-author
lang: en
tags: [opentofu, ci, azure]
summary: "Every pull request gets a plan, a policy check and a summary, never the full plan text."
image: /media/dc52c9498729-railway-bridge.jpg
image_alt: "A steam train with red carriages crossing a long, low bridge"
---

Infrastructure changes deserve the same review as code. Here every pull request runs `tofu plan` for each environment and posts a short summary.

![A steam train with red carriages crossing a long, low bridge](/media/dc52c9498729-railway-bridge.jpg)

## The plan job

```bash
tofu -chdir=infra/app init -input=false -lockfile=readonly
tofu -chdir=infra/app plan -input=false -lock=false -out=test.tfplan
tofu -chdir=infra/app show -json test.tfplan > test.plan.json
```

The plan identity can only read, so it plans without a lock.

## A resource worth reviewing

```hcl
resource "azurerm_container_app" "web" {
  name                         = "ca-${local.name}"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  ingress {
    external_enabled = true
    target_port      = 8080
  }
}
```

## Never post the plan

The repository is public, and plans can hold values. The summary lists addresses and counts only. Policies, written in Rego, run on the plan JSON; see [the baseline](/blog/a-small-rego-baseline-for-infrastructure).
