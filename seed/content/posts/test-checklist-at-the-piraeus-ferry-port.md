---
title: "A test checklist written at the Piraeus ferry port"
slug: test-checklist-at-the-piraeus-ferry-port
date: 2024-07-03
author: example-author
lang: en
tags: [testing, notes, travel]
summary: "Waiting for a ferry in Piraeus, I wrote down the few checks I run before every deploy of my small sites."
image: /media/0b5389dec5f5-ferry.jpg
image_alt: "A large white ferry with a blue stripe moored at a quay in Piraeus, with the city behind it"
---

I got to Piraeus far too early for my boat, so I sat on a bench facing a big white ferry and wrote the list I had been meaning to write for a year: what I actually check before I deploy one of my small sites.

![A large white ferry with a blue stripe moored at a quay in Piraeus, with the city behind it](/media/0b5389dec5f5-ferry.jpg "The ferry I watched for an hour while writing the list")

## Before every deploy

A ferry crew walks the same rounds before every departure, and nobody thinks that is boring. My rounds are short:

- The build passes with warnings treated as errors.
- The tests pass, including the slow browser tests I am tempted to skip.
- The home page, one post and the feed load on my phone.
- The pages still work with JavaScript turned off.

None of this is clever. The point is that it happens every time, not only when I feel nervous.

## Why I wrote it down

For years the list lived in my head, which means it had a different length depending on the day. On a tired evening it was one item: looks fine. Twice that cost me a broken feed that nobody told me about for weeks.

Now the list is a file in the repository, and the steps a machine can do are in a script. What is left for me is the part that needs eyes: does the page look right, and can I read it in the sun on a phone screen.

The ferry left on time. I went to find mine.
