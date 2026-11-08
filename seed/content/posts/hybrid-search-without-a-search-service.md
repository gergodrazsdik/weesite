---
title: "Hybrid search without a search service"
slug: hybrid-search-without-a-search-service
date: 2026-08-03
updated: 2026-08-20
author: example-author
lang: en
tags: [search, dotnet, performance]
summary: "SQLite for keywords, a small embedding model for meaning, and rank fusion to merge them."
---

A personal site doesn't need a search cluster. Everything here runs inside the web app.

## Keywords

```sql
CREATE VIRTUAL TABLE chunks USING fts5(
    title, heading, body,
    tokenize = 'porter unicode61 remove_diacritics 2');

SELECT heading, snippet(chunks, 2, '[', ']', '…', 24)
FROM chunks WHERE chunks MATCH '"rendering"*'
ORDER BY bm25(chunks, 10.0, 4.0, 1.0);
```

## Meaning

A 34 MB embedding model turns each section into 384 numbers. A query finds sections with similar numbers, even without shared words.

## Merging

```csharp
var score = 1.0 / (60 + rank);
```

Reciprocal rank fusion adds that up over both rankings. It needs no tuning and no score normalization.
