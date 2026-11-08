---
title: "Chiseled .NET images in practice"
slug: chiseled-net-images-in-practice
date: 2026-05-20
author: example-author
lang: en
tags: [dotnet, containers, security]
summary: "No shell, no package manager, and what that changes."
---

Chiseled images carry only what the .NET runtime needs. There is no shell to exec into, which is the point.

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Weesite.Web.dll"]
```

## Surprises

1. **No fonts.** Anything that draws text needs its own font files and a Fontconfig configuration.
2. **Read-only `/tmp`.** Libraries that cache to `/tmp` need another place.
3. **Files added from URLs** arrive readable by root only; set their mode.

## Debugging without a shell

Logs and traces do most of the work. For the rest, run the same image with a debug sidecar locally.
