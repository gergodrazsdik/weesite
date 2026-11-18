# Connect AI apps over MCP

Weesite has two [Model Context Protocol](https://modelcontextprotocol.io/) endpoints. Use them to let an AI app, such as Claude Code, Claude or VS Code, read the site or work on it for you.

| Endpoint | For | Sign-in | Tools |
|---|---|---|---|
| `https://<site>/mcp` | anyone | none | `search_content`, `get_content`, `list_content`: the public posts and pages |
| `https://<site>/mcp/editor` | editors | the admin sign-in (OAuth) | the tools below, with the editor's rights |

Both use Streamable HTTP. Replace `<site>` with the site's address: its domain, or the Container Apps hostname on test.

## The editor endpoint

An app connecting to `/mcp/editor` gets a 401 that tells it where to sign in. It then registers itself, opens the site's sign-in in your browser, and shows you a consent page:

- **Who asks:** the app's name, and the address you return to after deciding. An app that registered itself is marked as one Weesite can't verify. Allow it only if you just started it.
- **What it may do:**
  - *Drafts only* (the default): read posts and pages, create drafts, change drafts and upload media. It can't change anything visitors see.
  - *Drafts and edits*: also change published and scheduled posts and pages, which keep their status. It can't publish, schedule, unpublish or archive.
  - *Drafts, edits and publishing*: also publish, schedule, unpublish and archive, as you can in the editor.

Editors sign in as they do for `/admin`: the same providers and allowlist. Microsoft work accounts need their tenant in `Authentication__Microsoft__TrustedTenants` ([deploy guide](deploy.md#admins-and-editors)). Authors get their own posts only, as in the editor. "Editor" here means anyone who may sign in to `/admin`, an admin or an author; the endpoint tells the app which one connected it and what you allowed it.

| Tool | What it does |
|---|---|
| `list_items` | Posts and pages, drafts and scheduled ones included, by kind, status or text. Each has a path, the ID the other tools take. In pages of up to 200, with the total and the next page's offset. |
| `get_item` | A post or page as stored (front matter and Markdown), and its version. |
| `list_media` | Files in Media, with the Markdown that shows each, an image with the description it was uploaded with. In pages, like `list_items`. |
| `list_tags` | The tags posts use, most used first: each one's count of published posts, of the editor's own unpublished posts, and its colour and description. Apps reuse them and add a new tag only when none fits. |
| `create_item` | A new post or page from a Markdown file with front matter. It's always a draft. A post in another language than the site's sets `lang`, such as `lang: hu` ([Languages](languages.md)). |
| `update_item` | Replaces an item's front matter and text, saved only at the version read. The status stays. Apps allowed drafts only change drafts only. Text the same as the stored item's makes no new version (`unchanged` in the result). |
| `set_status` | Publishes, schedules, unpublishes or archives. Only for apps allowed to publish. |
| `create_media_upload` | Returns a one-time address to upload an image or file of up to 20 MB with a plain HTTP `PUT`, so the file doesn't pass through the model ([Upload larger files](#upload-larger-files)). Images need alt text. With `replaces`, the upload replaces a stored file as `replace_media` does, under the same rules. |
| `upload_media` | Uploads an image or file of up to 10 MB, sent in the call as base64. The model writes out every character, so in practice it suits only small files, such as a small SVG. Images need alt text, which is kept with the image. |
| `replace_media` | Replaces a file with a new one, such as an image still being worked on: the new file gets its own address, every post and page that used the old one is saved with the new one, and the old file is archived. Admins only; an app allowed drafts only can do it only when drafts alone use the file. Posts keep their own alt text: only the address changes, and a new description is stored with the file for the next time it's chosen. The new file comes as base64, like `upload_media`'s; for larger ones, use `create_media_upload` with `replaces`. |
| `preview_item` | Renders a stored item, or text not saved yet, as the site would show it, with the problems the site would warn about (images without alt text, unknown media, a summary over 160 characters). For a stored item, also its preview page's address. |
| `list_versions` | An item's stored versions, newest first. |
| `get_version_diff` | What changed between two versions, or a version and the current one: the changed lines with three around each. |
| `markdown_guide` | The Markdown posts and pages may use on this site: the extensions, links, images and captions, and code blocks with the languages the site highlights and an optional file name. |

A summary can be at most 160 characters, as in the editor.

Every write appears in **Audit log** with the app's name, such as `posts/hello.md (via Claude Code)`.

### Revoke an app

**Your profile → Connected apps** lists the apps you allowed, what each may do, and when it last refreshed its access. **Revoke** ends an app's access: its refresh token stops at once, and its access tokens within 30 seconds. Admins see every editor's apps under **Editors**, and may revoke any. Removing an editor revokes their apps.

To change what an app may do, revoke it and connect again.

An app's access ends when it hasn't used it for 30 days, or 90 days after you allowed it, however often it's used; then the app asks you to sign in again. Signing out everywhere (Your profile) ends your sign-ins in browsers, not your apps' access. Weesite keeps the registration of an app that registered itself for a month after its last access ended, and deletes it sooner, after a day, when nobody ever allowed the app.

### Upload larger files

An app that can run commands, such as Claude with code execution or Claude Code, uploads files with `create_media_upload`. It gives the file name and, for an image, its alt text, and gets back an address, an `Authorization` header and a curl command:

```sh
curl --fail-with-body -T hero.jpg -H "Authorization: Upload <token>" https://<site>/mcp/editor/upload
```

The answer is the stored file and the Markdown that shows it. The address takes one file, once, within 10 minutes, and stops working when you revoke the app. Each connected app can hold five unused addresses at a time; an app you connected twice, such as on two computers, has five for each connection.

To replace a file, such as an image still being worked on, the app gives `replaces`, the stored file's name from `list_media`. The upload then does what `replace_media` does, and answers as it does. The rules are the same: admins only, and an app allowed drafts only can replace only files that drafts alone use.

The command runs where the app runs commands, so that place has to reach your site. Claude's code execution sandbox reaches only allowed domains: add your site's domain in Claude's settings (**Capabilities**). Claude Code runs commands on your machine, or in its sandbox when you turned that on, where the domain has to be allowed as well.

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

An app allowed to publish can call `set_status` without asking you. Tools carry a hint that they change things, but each client decides from its own permission settings whether to ask. To have an app ask every time, set it in the client. Allowing drafts only avoids the question altogether.

**Claude Code:** add an `ask` rule to `.claude/settings.json` in a project, or to `~/.claude/settings.json` for all of them:

```json
{
  "permissions": {
    "ask": ["mcp__my-site__set_status", "mcp__my-site__update_item"]
  }
}
```

Use the name you gave the server. `update_item` is in the list because it can change a published post. To be asked before files are replaced, add `replace_media` and `create_media_upload` too; the second one also asks before new uploads.

**Claude and VS Code** ask before a tool runs unless you allowed it, per tool or for the server. Keep `set_status` set to ask.

## Troubleshooting

- **The first connection times out**: an environment that scales to zero, such as test, stops after a few idle minutes and takes about 40 seconds to start again. Open the site once, wait for it to load, then connect; or retry.
- **"This app may write drafts only"** or **"This app may edit but not publish"**: the app was allowed less than it tried. Do it in the editor, or revoke the app under Your profile and connect again with the choice you need.
- **The sign-in ends on "Not allowed in"**: the address you signed in with isn't an editor, or it's a Microsoft work account from a tenant that isn't trusted.
- **"Too many requests"**: the site limits each address to 60 calls a minute on `/mcp` and to upload addresses, and 300 on `/mcp/editor` with a token (`RateLimits:McpPerMinute`, `RateLimits:McpEditorPerMinute`). Wait a minute.
- **An upload answers 401**: the address was used already, expired after 10 minutes, or the app's access ended. Ask for a new one; the answer says which.
- **New tools don't appear after an update**: clients read the tool list when they connect. Reconnect the server.
- **"The app that sent you here isn't registered"**: the app's registration is unknown to the site (it was deleted after a month without access, see [Revoke an app](#revoke-an-app)), or its metadata document couldn't be read. Remove the server in the app and add it again.
