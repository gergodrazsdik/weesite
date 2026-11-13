---
title: "Backups and an old bank in Québec City"
slug: backups-and-an-old-bank-in-quebec-city
date: 2025-10-19
author: example-author
lang: en
tags: [tooling, notes, cities]
summary: "A solid stone bank building in Québec City made me wonder whether my site backups could actually be restored."
image: /media/cee2d28293a3-old-bank.jpg
image_alt: "The ornate stone front of an old bank building on a street corner in Québec City, with a classical pediment and a sports car parked in front"
---

Walking through the old town of Québec City, I stopped in front of an old bank building. Heavy stone, columns, a carved pediment: a building designed to tell you that your money is safe. A small convertible was parked in front of it, which made the building look even more serious.

![The ornate stone front of an old bank building on a street corner in Québec City, with a classical pediment and a sports car parked in front](/media/cee2d28293a3-old-bank.jpg "Built to look like it will never lose anything")

## Looking safe is not being safe

Standing there, I realised I had no idea whether my own backups worked. I had a nightly job that copied the content and media of my small sites to cheap storage somewhere else. I had a green tick in a log. I had never once restored from it.

That evening I tried, and it worked, but only just: one folder was missing, because I had moved it months earlier and never updated the script.

## What I do now

- **Back up the source, not only the output.** The Markdown files and media are what matter; the built site can be rebuilt.
- **Keep one copy somewhere else.** A different provider and a different account.
- **Restore on a schedule.** Once a month I restore the latest backup into a temporary folder and build the site from it.
- **Check the size.** If the backup suddenly shrinks, something is wrong.

The restore test takes ten minutes. It is the only part of the routine that actually tells me anything; the rest only tells me a job ran.

A bank looks safe because of its walls. A backup is safe only once you have opened the vault and found what you put in it.
