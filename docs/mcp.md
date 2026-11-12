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
  - *Read, write drafts and upload media* (the default). It can create drafts and change items that are still drafts. You publish in the editor.
  - *Also edit, publish, schedule and archive*, as you can in the editor.

Editors sign in as they do for `/admin`: the same providers and allowlist. Microsoft work accounts need their tenant in `Authentication__Microsoft__TrustedTenants` ([deploy guide](deploy.md#admins-and-editors)). Authors get their own posts only, as in the editor.

| Tool | What it does |
|---|---|
| `list_items` | Posts and pages, drafts and scheduled ones included, by kind, status or text. Each has a path, the ID the other tools take. |
| `get_item` | A post or page as stored (front matter and Markdown), and its version. |
| `list_media` | Files in Media, with the Markdown that shows each. |
| `create_item` | A new post or page from a Markdown file with front matter. It's always a draft. |
| `update_item` | Replaces an item's front matter and text, saved only at the version read. The status stays. |
| `set_status` | Publishes, schedules, unpublishes or archives. Only for apps allowed to publish. |
| `upload_media` | Uploads an image or file of up to 10 MB. Images need alt text. Larger files go through the editor's media page. |

Every write appears in **Audit log** with the app's name, such as `posts/hello.md (via Claude Code)`.

### Revoke an app

**Your profile → Connected apps** lists the apps you allowed, what each may do, and when it last refreshed its access. **Revoke** ends an app's access: its refresh token stops at once, and its access tokens within 30 seconds. Admins see every editor's apps under **Editors**, and may revoke any. Removing an editor revokes their apps.

To change what an app may do, revoke it and connect again.

An app's access ends when it hasn't used it for 30 days, and the app asks you to sign in again. Weesite keeps the registration of an app that registered itself for a month after its last access ended, and deletes it sooner, after a day, when nobody ever allowed the app.

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

Use the name you gave the server. `update_item` is in the list because it can change a published post.

**Claude and VS Code** ask before a tool runs unless you allowed it, per tool or for the server. Keep `set_status` set to ask.

## Troubleshooting

- **The first connection times out**: an environment that scales to zero, such as test, stops after a few idle minutes and takes about 40 seconds to start again. Open the site once, wait for it to load, then connect; or retry.
- **"This app may write drafts only"**: the app was allowed drafts only. Revoke it under Your profile and connect again, choosing publishing.
- **The sign-in ends on "Access denied"**: the address you signed in with isn't an editor, or it's a Microsoft work account from a tenant that isn't trusted.
- **"Too many requests"**: the site limits each address to 60 calls a minute on `/mcp`, and 300 on `/mcp/editor` with a token (`RateLimits:McpPerMinute`, `RateLimits:McpEditorPerMinute`). Wait a minute.
- **New tools don't appear after an update**: clients read the tool list when they connect. Reconnect the server.
- **"The app that sent you here isn't registered"**: the app's registration is unknown to the site (it was deleted after a month without access, see [Revoke an app](#revoke-an-app)), or its metadata document couldn't be read. Remove the server in the app and add it again.
