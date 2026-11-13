---
title: "Small deploys, like a long bridge"
slug: small-deploys-like-a-long-lake-bridge
date: 2025-02-11
author: example-author
lang: en
tags: [tooling, testing]
summary: "A long bridge over a lake is built one span at a time. I've started shipping software the same way."
image: /media/c413646f0110-lake-bridge.jpg
image_alt: "A long cable-stayed bridge stretching across a lake"
---

I once spent a ferry ride watching a long cable-stayed bridge over a lake slowly come closer. Seen whole, it looks like one enormous thing. Up close you can see it's a row of spans, each one carried by its own cables, each one finished before the next was started.

![A long cable-stayed bridge stretching across a lake](/media/c413646f0110-lake-bridge.jpg "A long bridge, one span at a time")

## One span at a time

That picture stayed with me, because my worst deploys have all been big ones: a week of changes merged at once, a release that touched everything, and then an afternoon of working out which part broke. The bridge suggests a better way. Each change should be small enough to stand on its own, and checked before the next one goes on top.

In practice that means:

- one logical change per pull request,
- every change deployed on its own, not batched,
- a quick check after each deploy before starting the next.

## A small check after each step

The check doesn't need to be clever. Mine is a few lines in a script that runs after every deploy:

```bash
#!/usr/bin/env bash
set -euo pipefail

url="${1:?usage: smoke.sh <base-url>}"
for path in / /health /feed.xml; do
  curl --fail --silent --show-error --output /dev/null "$url$path"
  echo "ok $path"
done
```

If one span fails, I know exactly which one, and it's small enough to roll back without thinking. The bridge still gets built; it just never has to be built twice.
