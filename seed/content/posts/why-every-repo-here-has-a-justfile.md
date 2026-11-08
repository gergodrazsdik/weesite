---
title: "Why every repo here has a justfile"
slug: why-every-repo-here-has-a-justfile
date: 2026-09-06
author: example-author
lang: en
tags: [tooling, notes]
summary: "One file that answers: how do I build, test and lint this?"
image: /media/116c3f9b1f40-keyboard.jpg
image_alt: "A close-up of a silver computer keyboard"
---

A `justfile` lists a repository's commands where everyone, and every agent, finds them.

![A close-up of a silver computer keyboard](/media/116c3f9b1f40-keyboard.jpg)

```just
# Build the solution
build:
    dotnet build Weesite.slnx -m:1

# Run the tests
test:
    dotnet test --solution Weesite.slnx

# Download the embedding model (pinned and hashed)
model:
    scripts/fetch-model.sh
```

## Better than a README section

Commands in a README drift. Commands in a `justfile` run, so they stay right.
