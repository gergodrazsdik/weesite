---
title: "Forms that work without JavaScript"
slug: forms-that-work-without-javascript
date: 2026-07-14
author: example-author
lang: en
tags: [web, accessibility]
summary: "A form that posts is a feature, not a fallback."
---

The theme switch and the search box on this site are plain forms. They work before any script loads, and when none does.

## The search form

```html
<form role="search" method="get" action="/search" aria-label="Search the site">
  <label for="q">Search the site</label>
  <input id="q" type="search" name="q">
  <button type="submit">Search</button>
</form>
```

## Why GET

Search results have a URL, so they can be bookmarked, shared and opened in a new tab.

## Enhancing later

A script may add instant results, but the form stays the contract: if the script fails, the form still works.
