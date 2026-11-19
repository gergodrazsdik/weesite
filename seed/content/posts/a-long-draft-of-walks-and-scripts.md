---
title: "A year of short walks, and the scripts that sort the photos"
slug: a-long-draft-of-walks-and-scripts
date: 2026-11-19
author: example-author
lang: en
tags: [notes, walking, photography]
summary: "A long draft: twelve months of short walks, the photos they gave, and the small scripts that keep the folders in order."
status: draft
---

This is a long draft. It stays in the editor and never reaches the public site, the feeds, search or the sitemap. It is long on purpose: prose, photos of different shapes, code, tables and lists, so the editor's split view has something to scroll through, and the preview runs much longer than the text wherever a photo sits.

For a year I took one short walk most days, rarely more than an hour, and nearly always with a camera. The walks were not the point at first. They were a way to stop working at a fixed time. The photos piled up, and by spring I had a folder nobody, including me, wanted to open. What follows is what the year looked like, and the few scripts that made the folder usable again.

## Starting with the lighthouse

![A white lighthouse on a rocky point, seen from the south-west, with the sea behind it](/media/82adc6cea959-lighthouse.jpg "The first walk that had a destination")

The first walk with a destination went to the lighthouse at the end of the point. It is a plain building, white, a little shorter than you expect. The path there is mostly gravel and takes forty minutes if you don't stop, which I always did.

What made that walk different was that I wrote down where I had been, how long it took and how many photos I kept. A line in a text file, nothing more. By the end of the month there were twenty-two lines, and the file had become more interesting than most of the photos.

The habit stuck because it cost nothing. A note at the end of the walk, while still standing outside, takes less than a minute. Done later, at a desk, it never happened at all.

## The folder problem

Cameras and phones name files by a counter or by the time, and neither says anything about where the photo was taken or why it was kept. After three months there were around two thousand files in one folder. Finding "the boats in the harbour in March" meant scrolling.

The fix was a naming rule and a script to apply it:

1. One folder per month, named `yyyy-mm`.
2. Inside, one folder per walk, named after the day and the place: `2026-03-14-harbour`.
3. Files keep their camera names, so nothing breaks when the camera's software looks for them again.
4. Anything not kept after a week is deleted, not archived.

The last rule mattered more than the others. An archive of photos you didn't want is still a folder you won't open.

### Sorting by date

The script reads each photo's date from its metadata and moves it into the month's folder. It doesn't guess the place; that comes from the walk log afterwards.

```bash filename="sort-photos.sh"
#!/usr/bin/env bash
# Moves each photo into a yyyy-mm folder by the date it was taken.
set -euo pipefail

source_dir=${1:?usage: sort-photos.sh <folder>}
for photo in "$source_dir"/*.jpg; do
  [ -e "$photo" ] || continue
  taken=$(exiftool -s3 -d '%Y-%m' -DateTimeOriginal "$photo")
  [ -n "$taken" ] || { echo "no date: $photo" >&2; continue; }
  mkdir -p "$source_dir/$taken"
  mv -n "$photo" "$source_dir/$taken/"
done
```

It refuses to overwrite anything (`mv -n`) and says which files it couldn't date. Those were nearly always screenshots, which belonged somewhere else anyway.

### Matching photos to walks

The walk log is a plain CSV: date, place, start and end time. A second script reads it and moves each month's photos into the walk whose time span contains them.

```python filename="match_walks.py"
import csv
import shutil
from datetime import datetime
from pathlib import Path


def load_walks(log: Path) -> list[tuple[datetime, datetime, str]]:
    walks = []
    with log.open(newline="", encoding="utf-8") as file:
        for row in csv.DictReader(file):
            start = datetime.fromisoformat(f"{row['date']}T{row['start']}")
            end = datetime.fromisoformat(f"{row['date']}T{row['end']}")
            walks.append((start, end, f"{row['date']}-{row['place']}"))
    return walks


def match(month: Path, walks: list[tuple[datetime, datetime, str]]) -> None:
    for photo in sorted(month.glob("*.jpg")):
        taken = datetime.fromtimestamp(photo.stat().st_mtime)
        for start, end, name in walks:
            if start <= taken <= end:
                (month / name).mkdir(exist_ok=True)
                shutil.move(photo, month / name / photo.name)
                break
```

It uses the file's modification time rather than the metadata, because the first script already sorted by the metadata and the camera sets both to the same moment. That shortcut would be wrong for edited photos, which is one more reason the edits live in a separate folder.

## Spring: the harbour and the bridge

![Small boats moored close together in a harbour, their masts reflected in the still water](/media/ad095e9b247e-harbour-boats.jpg "The harbour on a still morning in March")

By March the walks had a rotation: the harbour on calm mornings, the lake when it was windy, the forest when it rained. The harbour gave the most photos and kept the fewest. Boats are easy to photograph and hard to photograph well, and most mornings looked like the morning before.

The log made that visible. Counting kept photos per place showed that the harbour walks kept about one photo in fifteen, the forest one in five.

| Place | Walks | Photos taken | Photos kept | Kept |
| --- | ---: | ---: | ---: | ---: |
| Harbour | 41 | 1,230 | 82 | 7% |
| Lake and bridge | 37 | 610 | 71 | 12% |
| Forest | 52 | 940 | 188 | 20% |
| Lighthouse | 19 | 380 | 44 | 12% |
| Town | 64 | 1,410 | 97 | 7% |

The numbers aren't a judgement of the places. The forest changes every week; the harbour mostly changes with the light, and the light repeats.

![A long road bridge crossing a wide lake, seen from the water on a clear day](/media/c413646f0110-lake-bridge.jpg "The bridge from the lake path")

The lake walk crosses under the bridge about halfway. It is long enough that the far end disappears in haze on most days, and the walk back always feels shorter than the walk out, though the log says it takes the same time.

> The best photo of the bridge was taken on the way back, on a day I had decided not to take any.

## Summer: the trail by the water

![A wooden trail along a lake shore, with reeds on one side and calm water on the other](/media/06bd645d47dc-lake-trail.jpg){.right .medium}

In summer the walks moved earlier, before the heat, and the lake trail became the default. It is flat, shaded for most of its length, and busy after nine, so the photos from June and July are nearly all from before eight in the morning.

The trail has a boardwalk section over the reeds, and on still mornings the water is smooth enough that the reflection is sharper than the reeds themselves. Those photos are hard to tell the right way up, and two of them hang upside down at home without anyone noticing.

That photo beside this text is placed on the right, at medium size, which the site allows with braces after the image. Text flows around it on a wide screen and it takes the full width on a phone.

Summer was also when the log grew a column for the weather. It started as a joke and turned out to be the best predictor of which walks kept photos: overcast mornings kept almost twice as many as clear ones.

The weather column changed the schedule more than any of the scripts did. On a clear morning I would take the shorter walk and leave the camera; on an overcast one, the longer walk and two spare batteries.

## The configuration, such as it is

Both scripts read one small configuration file, so the folders and the log can move without editing code.

```yaml filename="walks.yaml"
photos: ~/pictures/walks
log: ~/notes/walks.csv
keep_days: 7
places:
  - harbour
  - lake
  - forest
  - lighthouse
  - town
```

And the justfile that ties them together, so the whole thing is one command at the end of a week:

```just filename="justfile"
# Sort this week's photos into months, then into walks.
sort:
    ./sort-photos.sh ~/pictures/walks/inbox
    python3 match_walks.py --config walks.yaml

# Delete what wasn't kept within the week.
tidy:
    python3 match_walks.py --config walks.yaml --tidy
```

## Autumn: the forest

![The sun setting behind tall trees at the edge of a forest, with long shadows across the grass](/media/46ff964dab23-forest-sunset.jpg "The forest edge in October")

Autumn was the forest's season. The walks got shorter as the days did, and more of them ended at the forest edge in the last light, which is the only time the place looks the way it does in memory.

The spruce stand in the middle of the forest is dark even at noon. Photos there need a slow shutter and a steady hand, and most of mine have neither. The ones that worked were taken leaning against a trunk, which in a spruce forest is never far away.

![A dense spruce forest, the trunks straight and close together, light coming through high above](/media/c0d16c7a20e9-spruce-forest.jpg)

The forest walks also produced the year's only real problem with the scripts. A camera clock that had drifted by an hour after the change to winter time put a week of photos into the wrong walks. The fix was a line in the log for the offset, and a rule to check the clock when the time changes:

- Check the camera's clock on the first walk after each time change.
- Note any offset in the log, not in the photos.
- Never edit the photos' dates; correct the walk times instead.

## What the year added up to

Three hundred and twelve walks, in the end, and a little over five hundred photos kept out of nearly five thousand taken. The scripts are under a hundred lines together. The log is the part I would keep if I could keep only one: it is the only record of where the time went, and it has outlived two cameras and one phone.

| Month | Walks | Kept | Longest walk |
| --- | ---: | ---: | --- |
| January | 21 | 31 | Lighthouse, 74 min |
| February | 19 | 28 | Harbour, 58 min |
| March | 27 | 49 | Lake and bridge, 81 min |
| April | 28 | 47 | Forest, 66 min |
| May | 30 | 52 | Lake and bridge, 90 min |
| June | 29 | 44 | Lake trail, 70 min |
| July | 26 | 39 | Lake trail, 65 min |
| August | 24 | 36 | Forest, 77 min |
| September | 27 | 51 | Forest, 84 min |
| October | 30 | 63 | Forest, 88 min |
| November | 31 | 42 | Town, 55 min |
| December | 20 | 30 | Lighthouse, 79 min |

A few things I would do differently:

- Start the log on day one, not after a month.
- Write the weather down from the beginning.
- Delete faster. Seven days was generous; three would have been enough.
- Keep the scripts boring. Every clever feature I added in the first month was gone by the third.

None of this needed special software. A text file, two short scripts and a rule about deleting were enough to turn a folder nobody opened into something I look at most weeks.

Next year the plan is the same walks, the same log, and fewer photos of boats.
