---
title: "Azure Container Apps on a hobby budget"
slug: azure-container-apps-on-a-hobby-budget
date: 2026-05-02
author: example-author
lang: en
tags: [azure, cost]
summary: "A personal site for the price of a coffee a month."
---

Container Apps bills per second of use, and its free grant covers a small site.

## Where the money goes

| Item | Monthly |
| --- | --- |
| Container Apps (0.25 vCPU, 0.5 GiB, one replica) | within the free grant |
| Storage (content, media, state) | under 1 € |
| Key Vault operations | cents |
| DNS zone | about 0.50 € |

## Keeping it small

- One replica, scaled to one, never to zero: cold starts hurt more than the cost.
- No search service: [search runs in the app](/blog/hybrid-search-without-a-search-service).
- No CDN: the site is small, and the app sends good cache headers.
