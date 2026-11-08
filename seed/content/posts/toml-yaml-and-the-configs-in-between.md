---
title: "TOML, YAML and the configs in between"
slug: toml-yaml-and-the-configs-in-between
date: 2026-09-20
author: example-author
lang: en
tags: [tooling]
summary: "Choosing a config format by what it gets wrong."
---

Every config format has a failure mode. Pick the one whose failure you can live with.

## TOML

```toml
[files]
extend-exclude = ["LICENSE", "docs/design/brief.md"]

[default.extend-words]
weesite = "weesite"
```

Clear for flat settings; deep nesting gets awkward.

## YAML

```yaml
on:
  pull_request:
    paths: ["src/**", "tests/**"]
```

Readable, but indentation is syntax, and `no` used to mean false.

## Protocol Buffers

For data that crosses a wire, a schema beats both:

```protobuf
syntax = "proto3";

message Post {
  string slug = 1;
  string title = 2;
  repeated string tags = 3;
}
```
