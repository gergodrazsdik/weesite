---
title: Notes on static rendering
slug: notes-on-static-rendering
date: 2026-02-03
author: example-author
lang: en
tags: [web, performance]
summary: Why every page is plain server-rendered HTML.
---

Every page is rendered on the server as plain HTML. Scripts only enhance it, so the site works without JavaScript, loads fast and reads well for people, search engines and agents alike.

## A list

- Semantic HTML
- Visible focus
- Contrast that passes WCAG AA

## Some code

```csharp
var greeting = "Hello";
Console.WriteLine(greeting);
```
