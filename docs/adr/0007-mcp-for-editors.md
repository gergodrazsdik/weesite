# 0007: MCP for editors

- Status: accepted
- Date: 2026-11-11
- Tracker: T-0082

## Context and problem statement

Editors want to draft, edit, schedule and publish posts and upload media from an MCP client (Claude, Claude Code, VS Code and others), signing in as they do for `/admin` and keeping their role. The public MCP endpoint (`/mcp`) is anonymous and read-only, and stays that way.

MCP's authorization specification (2025-11-25) makes the MCP server an OAuth 2.1 protected resource:

- the client finds the authorization server through Protected Resource Metadata (RFC 9728) and Authorization Server Metadata (RFC 8414);
- it registers itself with a Client ID Metadata Document (its `client_id` is an HTTPS URL that describes it) or with Dynamic Client Registration (RFC 7591);
- it signs the user in with the authorization code flow and PKCE, naming the resource it wants a token for (RFC 8707).

Google and Microsoft support neither way of registering, since their clients are registered by hand. Their tokens are also for their own APIs, not for Weesite. So something has to issue tokens for Weesite.

## Considered options

1. **Weesite's own small authorization server.** Weesite registers clients and issues its own tokens. Signing in goes through the existing admin sign-in: the same Google and Microsoft providers, allowlist and roles.
2. **OpenIddict as the authorization server**, with stores for Table Storage.
3. **Personal access tokens:** an editor creates a token in the admin and pastes it into the client as a bearer header.
4. **An external identity provider with client registration** (Entra External ID, Auth0 and the like).

## Decision outcome

Chosen option: 1, Weesite's own small authorization server, for these reasons:

- Only one flow is needed (authorization code with PKCE, plus refresh), and the sign-in, the allowlist and the roles already exist.
- OpenIddict brings a framework and its store interfaces for one flow, and doesn't read Client ID Metadata Documents.
- Personal access tokens are long-lived secrets in config files, and clients that take only OAuth (claude.ai connectors) can't use them.
- An external provider adds a service, a cost and a second user directory to keep in step with the editor list.

| Topic | Decision |
| --- | --- |
| Endpoint | `/mcp/editor`: Streamable HTTP, stateless, with the editor tools only. Bearer tokens only, no cookie. `/mcp` doesn't change. |
| Discovery | `/.well-known/oauth-protected-resource/mcp/editor`, served by the MCP SDK's authentication handler, which also sends the `WWW-Authenticate` challenge. `/.well-known/oauth-authorization-server` lists Weesite's endpoints. |
| Clients | Client ID Metadata Documents: Weesite fetches the document with limits on time and size, HTTPS only and no private addresses, then caches it. Dynamic registration (`/oauth/register`) for clients without them. Redirect URIs must be HTTPS or loopback HTTP, and must match exactly. |
| Sign-in and consent | `/oauth/authorize` sends a visitor who isn't signed in through the admin sign-in. A signed-in editor then sees a consent page that names the client, the host it returns to, and the role it gets. PKCE with S256 is required. Codes are single-use and valid for 60 seconds. |
| Tokens | Access tokens last 1 hour. They are opaque, protected with Data Protection (the keys the app already keeps), and bound to the `/mcp/editor` resource. Refresh tokens are kept hashed, one grant per client and editor, in Table Storage. They rotate on every use, and reusing an old one revokes the grant. Each request looks up the editor's role through the cache the cookie uses, so removing an editor or changing their role takes effect within 30 seconds. |
| Revocation | **Your profile** lists the editor's connected apps, each with Revoke. Admins see every editor's apps under **Editors**. Removing an editor revokes their grants. Clients can revoke their own tokens (RFC 7009, `/oauth/revoke`). |
| Tools | `list_items` and `get_item` (Markdown with front matter, plus the ETag), `create_item`, `update_item` (with the ETag, like the editor's conflict check), `set_status` (draft, scheduled with a time, published, archived), `list_media` and `upload_media`. The rights are the admin's (`EditorRights`): authors write only their own posts. Deleting items, settings and editors stays in `/admin`. |
| Audit | Grants, revocations and every write tool go into the audit log, with the editor and the client's name. |
| Limits | A rate limit per grant, plus the per-address limit `/mcp` has. Uploads get the admin's checks and size limit. |

### Consequences

- Weesite carries its own token code, which is security-sensitive. Tests cover each rule above:
  - PKCE;
  - the exact redirect match;
  - code replay;
  - refresh rotation and reuse;
  - the audience;
  - an expired token;
  - a removed editor.
- An agent can publish. The publish and schedule tools are marked as not read-only, so clients ask before calling them, and the audit log shows which client did it.
- Admin saves aren't audited today; MCP writes are, because an agent acting for an editor is worth a record.
- Grants live in Table Storage next to the editors, so they aren't part of a site export.

## More information

- [MCP authorization specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization)
- [RFC 9728: OAuth 2.0 Protected Resource Metadata](https://www.rfc-editor.org/rfc/rfc9728)
- [RFC 8414: OAuth 2.0 Authorization Server Metadata](https://www.rfc-editor.org/rfc/rfc8414)
- [RFC 7591: OAuth 2.0 Dynamic Client Registration](https://www.rfc-editor.org/rfc/rfc7591)
- [RFC 8707: Resource Indicators for OAuth 2.0](https://www.rfc-editor.org/rfc/rfc8707)

## Amendment (2026-11-11): Weesite serves the protected resource metadata

Weesite serves the protected resource metadata itself (`/.well-known/oauth-protected-resource/mcp/editor`, and without the path for clients that look there). Its bearer scheme sends the 401 with `resource_metadata`. The MCP SDK's authentication handler only challenges and doesn't validate tokens, so pairing it with a bearer scheme would mean two challenges on one endpoint. One endpoint process serves both tool sets: each request keeps only its endpoint's tools (`ConfigureSessionOptions`).
