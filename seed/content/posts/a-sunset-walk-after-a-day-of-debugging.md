---
title: "A sunset walk after a day of debugging"
slug: a-sunset-walk-after-a-day-of-debugging
date: 2026-07-03
author: example-author
lang: en
tags: [notes, dotnet, testing]
summary: "A flaky test, eight hours at the desk, and the answer that turned up on a walk by the harbour."
image: /media/ee3a9b622ebb-sunset.jpg
image_alt: "Small fishing boats on a calm sea at sunset"
---

I spent a whole day on one failing test. It passed on my machine, failed in CI about one run in five, and every fix I tried made it pass a few more times before failing again. By evening I'd stopped making progress and started making noise.

![Small fishing boats on a calm sea at sunset](/media/ee3a9b622ebb-sunset.jpg "The harbour, just after I gave up for the day")

## Giving up for an hour

So I closed the laptop and walked down to the harbour. The fishing boats were coming in, the sea was flat, and the sun was going down behind them. I didn't think about the test at all, or I thought I didn't.

Halfway back it came to me: the test compared a timestamp against the current time, and on a slow CI machine the gap was sometimes more than a second. The code was fine. The test was racing the clock.

## The fix

The answer was to stop reading the real clock and pass one in instead:

```csharp
public sealed class Greeting(TimeProvider clock)
{
    public string For(string name) =>
        clock.GetUtcNow().Hour < 12 ? $"Good morning, {name}" : $"Hello, {name}";
}
```

In the test, a fake time provider sets the time exactly, and the result no longer depends on how fast the machine is. It took ten minutes to write, after eight hours of not seeing it.

I don't want to make a rule out of one evening. But when I'm stuck now, I go for a walk sooner.
