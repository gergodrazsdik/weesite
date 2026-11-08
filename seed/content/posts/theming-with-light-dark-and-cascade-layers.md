---
title: "Theming with light-dark() and cascade layers"
slug: theming-with-light-dark-and-cascade-layers
date: 2026-06-30
author: example-author
lang: en
tags: [css, web, accessibility]
summary: "One set of tokens, two colour schemes, and a theme a site can override."
---

`light-dark()` picks one of two values by the current colour scheme, so every colour token can carry both.

```css
@layer tokens {
  :root {
    color-scheme: light dark;
    --color-bg: light-dark(#ffffff, #111318);
    --color-text: light-dark(#1a1d23, #e6e8eb);
  }

  :root[data-theme="dark"] {
    color-scheme: dark;
  }
}
```

## Layers keep overrides predictable

With `@layer reset, tokens, base, components;` a site's theme only has to override tokens, never fight specificity.

## Contrast

Every pair of text and background colours meets WCAG AA in both modes. The theme editor will check new colours the same way.
