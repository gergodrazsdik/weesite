# Connect AI apps over MCP

Weesite has two [Model Context Protocol](https://modelcontextprotocol.io/) endpoints. Use them to let an AI app, such as Claude Code, Claude or VS Code, read the site or work on it for you.

| Endpoint | For | Sign-in | Tools |
|---|---|---|---|
| `https://<site>/mcp` | anyone | none | `search_content`, `get_content`, `list_content`: the public posts and pages, with the special pages Site settings place (such as `/privacy`) |
| `https://<site>/mcp/editor` | editors | the admin sign-in (OAuth) | the tools below, with the editor's rights |

Both use Streamable HTTP. Replace `<site>` with the site's address: its domain, or the Container Apps hostname on test.

## The editor endpoint

An app connecting to `/mcp/editor` gets a 401 that tells it where to sign in. It then registers itself, opens the site's sign-in in your browser, and shows you a consent page:

- **Who asks:** the app's name, and the address you return to after deciding. An app that registered itself is marked as one Weesite can't verify. Allow it only if you just started it.
- **What it may do:**
  - *Drafts only* (the default): read posts and pages, create drafts, change drafts and upload media. It can't change anything visitors see.
  - *Drafts and edits*: also change published and scheduled posts and pages, which keep their status. It can't publish, schedule, unpublish or archive.
  - *Drafts, edits and publishing*: also publish, schedule, unpublish and archive, as you can in the editor, and read and moderate visitors' comments.
  - *Ask me in the app before anything is published*, an option of publishing: the app asks you before `set_status` publishes or schedules an item ([Ask before publishing](#ask-before-publishing)).

No app changes Site settings, whatever you allow: not even by replacing the file they use as the logo, favicon, banner or home page photo.

An app returns from the consent page to an HTTPS address, a loopback address on your computer (`http://127.0.0.1:<port>/…`), or its own scheme named after a domain in reverse order, such as `com.example.app:/callback` (RFC 8252); Cursor's `cursor://anysphere.…` addresses work too. An app that registers any other address is refused. When an app's request to sign in is malformed, the site shows what's wrong on its own page and doesn't send you back to the app.

Editors sign in as they do for `/admin`: the same providers and allowlist. Microsoft work accounts need their tenant in `Authentication__Microsoft__TrustedTenants` ([deploy guide](deploy.md#admins-and-editors)). Authors get their own posts only, as in the editor. "Editor" here means anyone who may sign in to `/admin`, an admin or an author; the endpoint tells the app which one connected it and what you allowed it.

| Tool | What it does |
|---|---|
| `list_items` | Posts and pages, drafts and scheduled ones included, by kind, status or text. Each has a path, the ID the other tools take. In pages of up to 200, with the total and the next page's offset. |
| `get_item` | A post or page as stored (front matter and Markdown), and its version. |
| `list_media` | Files in Media, with the Markdown that shows each, an image with the description it was uploaded with. In pages, like `list_items`. |
| `list_tags` | The tags posts use, most used first: each one's count of published posts, of the editor's own unpublished posts, and its colour and description. Apps reuse them and add a new tag only when none fits. |
| `create_item` | A new post or page from a Markdown file with front matter. It's always a draft. A post in another language than the site's sets `lang`, such as `lang: hu` ([Languages](languages.md)). |
| `update_item` | Replaces an item's front matter and text, saved only at the version read. The status stays. Apps allowed drafts only change drafts only. Text the same as the stored item's makes no new version (`unchanged` in the result). |
| `edit_item` | Changes part of an item without the app sending the whole file: up to 50 edits, each replacing an exact `old_text` with a `new_text` in the file as `get_item` returns it, front matter included, in order and all or none. Each `old_text` must appear exactly once; when one doesn't, nothing is saved, and the error names the edit and, for text that appears more than once, its lines. Saved at the version read; when someone saved the item since, the edits are applied to the new version if every `old_text` still appears once there (`rebased` in the result), and refused otherwise. The result has the lines that changed (`diff`). Otherwise as `update_item`: the status stays, the same checks apply, and an edit that changes nothing makes no new version. |
| `set_tags` | Adds tags to an item and removes others, leaving the rest of it as it is, so an app needn't send the whole file again to change a tag. It applies to the current version, so it needs no version, and keeps a tag someone added meanwhile. Returns the tags now, and those no other item carries (`newTags`), to catch a misspelt tag. Apps allowed drafts only change drafts only. |
| `set_status` | Publishes, schedules, unpublishes or archives. Only for apps allowed to publish. With the consent's option to ask first, it asks you in the app before it publishes or schedules ([Ask before publishing](#ask-before-publishing)). |
| `create_media_upload` | Returns a one-time address to upload an image or file of up to 20 MB with a plain HTTP `PUT`, so the file doesn't pass through the model ([Upload larger files](#upload-larger-files)). Images need alt text. With `replaces`, the upload replaces a stored file as `replace_media` does, under the same rules, checked when the address is made and again when the file arrives. |
| `upload_media` | Uploads an image or file of up to 10 MB, sent in the call as base64. The model writes out every character, so in practice it suits only small files, such as a small SVG. Images need alt text, which is kept with the image. |
| `replace_media` | Replaces a file with a new one, such as an image still being worked on: the new file gets its own address, every post and page that used the old one is saved with the new one, and the old file is archived. Admins only; an app allowed drafts only can do it only when drafts alone use the file. No app replaces a file that Site settings use (the logo, favicon, banner or home page photo): change those in `/admin`, under Site settings. Posts keep their own alt text: only the address changes, and a new description is stored with the file for the next time it's chosen. The new file comes as base64, like `upload_media`'s; for larger ones, use `create_media_upload` with `replaces`. |
| `preview_item` | Renders a stored item, or text not saved yet, as the site would show it, with the problems the site would warn about (images without alt text, unknown media, a summary over 160 characters). For a stored item, also its preview page's address, which needs a browser signed in to `/admin`; `create_preview_link` gives one an app can open. |
| `create_preview_link` | A link to a saved draft's or scheduled item's preview page that works without signing in, for an hour, so an app can open it in its own browser, or fetch it, and see the item as visitors will: layout, styles, images and all. It shows the last saved version, so the app reloads it after each save. Anyone with the link can open it until it expires, the app is disconnected or the editor removed; it's never cached or indexed. For a public item it gives the public address. Every link is in the audit log. |
| `list_versions` | An item's stored versions, newest first. |
| `get_version_diff` | What changed between two versions, or a version and the current one: the changed lines with three around each. |
| `list_comments` | Visitors' comments on posts the editor may moderate (admins every post's, authors their own), by status: `waiting` (the default: new ones, and edits of approved ones beside their published text), `pending`, `approved`, `rejected` (also lists `removed` ones: published, then taken off by an admin) or `all` (every comment, also the markers of published comments their writers deleted, status `deleted`, without name or text). Each comment has a `version` for `moderate_comment`. In pages, like `list_items`. Only for apps allowed to publish: reading comments is for moderating them. What visitors wrote (the name, the text and a waiting edit) comes under each comment's `visitorWritten`, below a note that says so, apart from the site's own fields: an app should treat it as data, not instructions ([Ask before publishing](#ask-before-publishing)). Contact messages never reach an app. |
| `moderate_comment` | Decides on a comment, with the `version` that `list_comments` gave. `approve` makes a waiting comment public, or a waiting edit its public text; it needs the version, so what's published is the text the app read. `reject` keeps a waiting comment off the site, or drops a waiting edit and keeps the public text. `remove` (admins only) takes a published comment off its post, with any edit waiting; a marker keeps its place where a later comment follows. With a version, a comment that changed since (its writer edited or deleted it, or someone else decided) is left alone, and the app lists it again. Only for apps allowed to publish. |
| `delete_comment` | Deletes a comment for good, whatever its status. Only for apps allowed to publish. |
| `markdown_guide` | The Markdown posts and pages may use on this site: the extensions, links, images and captions, and code blocks with the languages the site highlights and an optional file name. |

A summary can be at most 160 characters, as in the editor.

Every write appears in **Audit log** with the app's name, such as `posts/hello.md (via Claude Code)`.

### Revoke an app

**Your profile → Connected apps** lists the apps you allowed, where each comes from (such as claude.ai), what it may do, when you connected it and when it last refreshed its access (Last used). **Revoke** ends an app's access: its refresh token stops at once, and its access tokens within 30 seconds. Admins see every editor's apps under **Editors**, and may revoke any. Removing an editor revokes their apps.

To change what an app may do, revoke it and connect again.

An app's access ends when it hasn't used it for 30 days, or 90 days after you allowed it, however often it's used; then the app asks you to sign in again. Signing out everywhere (Your profile) ends your sign-ins in browsers, not your apps' access. Weesite keeps the registration of an app that registered itself for a month after its last access ended, and deletes it sooner, after a day, when nobody ever allowed the app.

### Upload larger files

An app that can run commands, such as Claude with code execution or Claude Code, uploads files with `create_media_upload`. It gives the file name and, for an image, its alt text, and gets back an address, an `Authorization` header and a curl command:

```sh
curl --fail-with-body -T hero.jpg -H "Authorization: Upload <token>" https://<site>/mcp/editor/upload
```

The answer is the stored file and the Markdown that shows it. The address takes one file, once, within 10 minutes, and stops working when you revoke the app. Each connected app can hold five unused addresses at a time; an app you connected twice, such as on two computers, has five for each connection.

To replace a file, such as an image still being worked on, the app gives `replaces`, the stored file's name from `list_media`. The upload then does what `replace_media` does, and answers as it does. The rules are the same: admins only, never a file Site settings use, and an app allowed drafts only can replace only files that drafts alone use. They're checked when the address is made and again when the file arrives.

The command runs where the app runs commands, so that place has to reach your site. Claude's code execution sandbox reaches only allowed domains: add your site's domain in Claude's settings (**Capabilities**). Claude Code runs commands on your machine, or in its sandbox when you turned that on, where the domain has to be allowed as well.

### The editing skill (experimental)

The editor endpoint also serves an [MCP skill](https://modelcontextprotocol.io/extensions/skills/overview), `weesite-editor`, through the skills extension (`io.modelcontextprotocol/skills`). A skill is a `SKILL.md` file an app loads when a task needs it. This one teaches the editing workflow behind the tools: read an item with `get_item` before changing it and save at its version, what to do on a conflict, drafts and publishing at each level of access, alt text and reusing media, reusing tags, and previewing before publishing. For the Markdown and the languages this site highlights it points to `markdown_guide`, which stays the place for them. The server's instructions stay as they are; the skill adds detail for apps that load skills.

- **Where:** `/mcp/editor` only, after the sign-in. The public `/mcp` serves no skills and doesn't declare the extension.
- **Which apps:** Claude and Claude Code support skills from MCP servers (in beta when this was written). Apps without support see the tools and instructions as before.
- **Experimental:** the extension is new and the SDK marks its support experimental. Weesite may change the skill, or how it's served, with the extension.

The skill ships with the app (`src/Weesite.Web/Skills/weesite-editor/SKILL.md`), so it changes with each release, and a test checks that every tool it names exists. Each file is listed with its SHA-256 digest and size, which apps check before loading it.

## Claude Code

```sh
claude mcp add --transport http my-site https://<site>/mcp/editor
```

Then run `/mcp` in Claude Code and choose the server to sign in. The name (`my-site` here) is yours to choose. Claude Code names the tools after it: `mcp__my-site__create_item`.

For the public endpoint, use `https://<site>/mcp`. It needs no sign-in.

## Claude

In Claude's settings, open **Connectors** and add a custom connector with the URL `https://<site>/mcp/editor`. Claude sends you through the sign-in and consent when you connect it.

## VS Code

Add the server to `.vscode/mcp.json` in a workspace, or to your user MCP configuration:

```json
{
  "servers": {
    "my-site": {
      "type": "http",
      "url": "https://<site>/mcp/editor"
    }
  }
}
```

Start it from the MCP server list. VS Code opens the sign-in in your browser.

## Ask before publishing

An app allowed to publish can call `set_status`, `moderate_comment` and `delete_comment` without asking you. Tools carry a hint that they change things, but each client decides from its own permission settings whether to ask. To have an app ask every time, set it in the client. Allowing drafts only avoids the question altogether.

**Ask me in the app before anything is published** on the consent page makes the site ask too: before `set_status` publishes or schedules a post or page, the app shows you "Publish 'Title' now?" or "Schedule 'Title' for 2026-12-01 09:00 UTC?", and the item changes only when you confirm. Decline, and nothing changes; the app is told so. Unpublishing and archiving don't ask. The question goes through MCP elicitation, on the 2026-07-28 protocol's multi round-trip requests, so it works in clients that support both. An app that can't ask can't publish or schedule with this option: it gets an error that says to do it in `/admin`, or to connect the app again without the option. The audit log has each answer (`mcp-publish-confirmed`, `mcp-publish-declined`) and each time an app couldn't ask (`mcp-publish-not-asked`).

The question is a convenience, not a safeguard: the app's own client shows it and sends back the answer, so a client that misbehaves could answer for you. What limits an app is the level you allowed it. The site checks that the answer is to its own question about that item, that version and that change, within 10 minutes, and that it's used once. To change the option, revoke the app and connect it again.

Such an app also reads visitors' comments, text that anyone can write. Weesite marks it as visitors' (`visitorWritten`) and tells the app to treat it as data, but a model can still be swayed by what it reads. Keep the tools that change your site set to ask, as below, when you have an app go through comments.

**Claude Code:** add an `ask` rule to `.claude/settings.json` in a project, or to `~/.claude/settings.json` for all of them:

```json
{
  "permissions": {
    "ask": [
      "mcp__my-site__set_status",
      "mcp__my-site__update_item",
      "mcp__my-site__edit_item",
      "mcp__my-site__moderate_comment",
      "mcp__my-site__delete_comment"
    ]
  }
}
```

Use the name you gave the server. `update_item` and `edit_item` are in the list because they can change a published post. The comment tools are in it because approving publishes a visitor's words on your site and deleting can't be undone. To be asked before files are replaced, add `replace_media` and `create_media_upload` too; the second one also asks before new uploads.

**Claude and VS Code** ask before a tool runs unless you allowed it, per tool or for the server. Keep `set_status`, `moderate_comment` and `delete_comment` set to ask.

## Troubleshooting

- **The first connection times out**: an environment that scales to zero, such as test, stops after a few idle minutes and takes about 40 seconds to start again. Open the site once, wait for it to load, then connect; or retry.
- **503 just after the site starts**: a new replica is up before its Key Vault secrets are in, and for up to 30 seconds the editor endpoint and the sign-in answer 503 with a `Retry-After` time. Try again after it; the public endpoint isn't affected.
- **"this app can't ask you"** from `set_status`: you chose to be asked before anything is published, and the app doesn't support MCP elicitation. Publish in `/admin`, or revoke the app and connect it again without the option.
- **"This app may write drafts only"** or **"This app may edit but not publish"**: the app was allowed less than it tried. Do it in the editor, or revoke the app under Your profile and connect again with the choice you need.
- **The sign-in ends on "Not allowed in"**: the address you signed in with isn't an editor, or it's a Microsoft work account from a tenant that isn't trusted.
- **"Too many requests"**: the site limits each address to 60 calls a minute on `/mcp`, 300 on `/mcp/editor` with a token, and 30 files a minute sent to upload addresses (`RateLimits:McpPerMinute`, `RateLimits:McpEditorPerMinute`, `RateLimits:McpUploadPerMinute`). Wait a minute.
- **"Site settings use this file"**: an app tried to replace the logo, favicon, banner or home page photo. Apps never change Site settings: choose the new file under Site settings in `/admin`, or replace it under Media.
- **"needs an app allowed to publish"** from `list_comments`: reading comments is part of moderating them. Moderate under Comments in `/admin`, or revoke the app and connect it again with publishing allowed.
- **An upload answers 401**: the address was used already, expired after 10 minutes, or the app's access ended. Ask for a new one; the answer says which.
- **New tools don't appear after an update**: clients read the tool list when they connect. Reconnect the server.
- **"The app that sent you here isn't registered"**: the app's registration is unknown to the site (it was deleted after a month without access, see [Revoke an app](#revoke-an-app)), or its metadata document couldn't be read. Remove the server in the app and add it again.
