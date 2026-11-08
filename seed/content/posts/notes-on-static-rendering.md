---
title: Notes on static rendering
slug: notes-on-static-rendering
date: 2026-02-03
author: example-author
lang: en
tags: [web, performance]
summary: Why every page is plain server-rendered HTML.
image: /media/c0f10a37852f-gradient.jpg
image_alt: A blue gradient, light at the top and dark at the bottom.
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

## An image and a download

![A blue square, the sample image](/media/b368a21f9e93-sample.png)

![A blue gradient from light at the top to dark at the bottom](/media/c0f10a37852f-gradient.jpg)

The same square as a [vector drawing](/media/0e3d1bf78702-diagram.svg), which downloads rather than opens.
