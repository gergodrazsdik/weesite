---
title: "Testing a Blazor SSR site with TUnit and Playwright"
slug: testing-blazor-ssr-with-tunit-and-playwright
date: 2026-06-08
author: example-author
lang: en
tags: [dotnet, testing, accessibility]
summary: "In-memory tests for endpoints, a real browser for pages, and axe on every page."
---

Two layers cover most of a server-rendered site: the app in memory for endpoints, and a browser for pages.

## In memory

```csharp
[Test]
public async Task Post_has_a_canonical_link()
{
    await using var factory = new SiteFactory();
    using var client = factory.CreateClient();

    var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

    await Assert.That(html).Contains("<link rel=\"canonical\"");
}
```

## In a browser

Locators by role and name double as accessibility checks: if the test can't find "Search the site", neither can a screen reader.

## Axe

Every page runs through axe in light and dark mode. It has already caught two unnamed search landmarks.
