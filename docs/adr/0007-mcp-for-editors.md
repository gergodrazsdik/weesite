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
| Discovery | `/.well-known/oauth-protected-resource/mcp/editor`, served by the MCP SDK's authentication handler, which also sends the `WWW-Authenticate` challenge. `/.well-known/oauth-authorization-server` lists Weesite's endpoints. **Changed:** Weesite serves the metadata and sends the challenge itself ([amendment, 2026-11-11](#amendment-2026-11-11-weesite-serves-the-protected-resource-metadata)). |
| Clients | Client ID Metadata Documents: Weesite fetches the document with limits on time and size, HTTPS only and no private addresses, then caches it. Dynamic registration (`/oauth/register`) for clients without them. Redirect URIs must be HTTPS or loopback HTTP, and must match exactly. **Changed:** a native app's scheme named after a domain is allowed too ([amendment, 2026-11-24](#amendment-2026-11-24-limits-per-address-and-hardening-t-0177)). |
| Sign-in and consent | `/oauth/authorize` sends a visitor who isn't signed in through the admin sign-in. A signed-in editor then sees a consent page that names the client, the host it returns to, and the role it gets. PKCE with S256 is required. Codes are single-use and valid for 60 seconds; a second exchange of a code revokes the grant it gave (RFC 6749 §4.1.2), so the used code is kept for a day. |
| Tokens | Access tokens last 1 hour. They are opaque, protected with Data Protection (the keys the app already keeps), and bound to the `/mcp/editor` resource. Refresh tokens are kept hashed, one grant per client and editor, in Table Storage. They rotate on every use, and reusing an old one revokes the grant. Each request looks up the editor's role through the cache the cookie uses, so removing an editor or changing their role takes effect within 30 seconds. |
| Revocation | **Your profile** lists the editor's connected apps, each with Revoke. Admins see every editor's apps under **Editors**. Removing an editor revokes their grants. Clients can revoke their own tokens (RFC 7009, `/oauth/revoke`). |
| Tools | `list_items` and `get_item` (Markdown with front matter, plus the ETag), `create_item`, `update_item` (with the ETag, like the editor's conflict check), `set_status` (draft, scheduled with a time, published, archived), `list_media` and `upload_media`. The rights are the admin's (`EditorRights`): authors write only their own posts. Deleting items, settings and editors stays in `/admin`. |
| Audit | Grants, revocations and every write tool go into the audit log, with the editor and the client's name. |
| Limits | A rate limit per grant, plus the per-address limit `/mcp` has. Uploads get the admin's checks and size limit. **Changed:** the limits are per address only, as the limiter runs before sign-in ([amendment, 2026-11-24](#amendment-2026-11-24-limits-per-address-and-hardening-t-0177)). |

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

## Amendment (2026-11-12): publishing is part of the consent

The tools' annotations don't make clients ask before publishing. A test on test confirmed it: Claude Code ran `set_status` without a prompt, because the client's permission settings decide. So the consent page asks what the app may do:

- **Drafts only** (the default): read, create drafts, edit items that are still drafts, and upload media.
- **Drafts and publishing:** as in the editor.

The grant and its tokens carry the choice, and the server enforces it. An app with drafts only can't change a published or scheduled item, and can't change any item's status. The docs also show how to make a client ask before `set_status`, for editors who want a prompt as well.

The servers also introduce themselves with the site's name and tagline from the site settings ("Example Site (editor)" on `/mcp/editor`), with instructions for their tools. The name of a connection, and so the prefix of its tools in a client, is the user's choice in the client.

## Amendment (2026-11-12): drafts and edits (F-0070)

A third choice sits between the two: **Drafts and edits** lets the app also change published and scheduled items, which keep their status, but not change any item's status. "Drafts and publishing" is now called **Drafts, edits and publishing**. Each choice on the consent page says what it allows and what it doesn't: the maintainer couldn't tell that drafts only could change drafts. Grants and access tokens issued before this keep their access (drafts only, or publishing).

## Amendment (2026-11-15): OpenIddict looked at again; dynamic registration deprecated

Option 2 was weighed again after the second security review round (T-0122), and Weesite keeps its own authorization server. OpenIddict would take over issuing and checking tokens and exchanging codes and refresh tokens, a few hundred of the about 2,400 lines. It would bring its store interfaces for Table Storage (more code than `OAuthStore` today), still has no Client ID Metadata Documents or dynamic registration of its own, so `ClientRegistry` stays, and moving to it would end every grant. The consent page, the access levels and the editor mapping stay either way.

The MCP specification of 2026-07-28 deprecates dynamic registration in favour of Client ID Metadata Documents, which Weesite already supports and advertises (`client_id_metadata_document_supported`, with `none` as the token endpoint auth method and `iss` on the redirect, RFC 9207). Dynamic registration stays for clients that don't use metadata documents yet, until July 2027 at the earliest (T-0134).

## Amendment (2026-11-17): upload addresses

`upload_media` takes the file as base64 in the tool call, and a model writes every character of it. In practice that limits it to a few KB: Claude reported that a 195 KB image, about 260,000 characters, couldn't be sent, so only small SVGs fit (T-0137). The bytes need a way round the model.

`create_media_upload` (file name, alt text) returns a one-time upload address: `PUT /mcp/editor/upload`, with an `Authorization: Upload <token>` header, and a curl command that sends a local file there. The app runs it from a shell. The answer is the stored file and its Markdown, as from `upload_media`.

- **The token is the only credential.** A cookie or the app's access token doesn't count. It goes in a header, not the address, so it stays out of request logs and traces, and the endpoint sends no CORS headers, so a page in a browser can't use it.
- **One file, once, for 10 minutes.** The token is 32 random bytes; Table Storage keeps only its SHA-256, in `oauthgrants` (partition `upload`), with the grant, the editor, the file name, the alt text and the file it replaces, if any. Using the address deletes it at the version read, so of two uploads at once only one gets it. A request without a length or over the limit is refused before the address is used, so it can be sent again; any other attempt uses it up. The OAuth clean-up deletes expired ones.
- **The app's access as it is now.** At upload, the grant must still exist and the editor must still be an editor, so revoking the app or removing the editor ends its addresses.
- **Nothing new allowed.** Every access level could already upload media. The file goes through the same checks as every upload, and the limit is the editor's 20 MB, as the bytes no longer come in JSON (F-0066).
- **Replacing (T-0138).** With `replaces`, a stored file's name, the upload replaces that file as `replace_media` does, under its rules: admins only, and an app allowed drafts only only when drafts alone use the file. They're checked when the address is made, so the app learns at once, and again at upload, as the role, the access or the file's uses may have changed by then. Because an address can do that, `create_media_upload` is marked destructive.
- **Limits.** At most five unused addresses per grant at once, so per app an editor connected (each consent is a grant of its own); the endpoint counts against the per-address limit of `/mcp`.
- **Audit.** Both the address (`mcp-upload-address`) and the upload (`mcp-media-uploaded`) go into the audit log with the app's name.

The alternatives: a shared access signature for Blob Storage would skip the checks, the metadata stripping and the WebP sizes, and open the storage account to the internet; a tool that fetches a file from an address would make the server fetch addresses an app chooses, and the files are on the editor's machine, not on the web. `upload_media` and `replace_media` keep taking base64, for small files and clients that can't run commands.

## Amendment (2026-11-24): limits per address, and hardening (T-0177)

**Limits.** The Limits row promised "a rate limit per grant, plus the per-address limit `/mcp` has". The limits are per client address only: the rate limiter runs before sign-in, so it can't tell grants apart, and counting per token there would give every made-up token a limit of its own. Requests to `/mcp/editor` with a bearer token count against `mcp-editor` (300 a minute per address), and files sent to upload addresses against `mcp-upload` (30 a minute per address), each apart from the public `/mcp` (60 a minute). A token's holder is limited by their address, as any client is; revoking the app is what ends a token. The upload addresses' "counts against the per-address limit of `/mcp`" in the amendment of 2026-11-17 is replaced by `mcp-upload`.

**Site settings.** Apps never change Site settings, at any access level. Since replacing a file also points the settings that use it at the new one, no app may replace a file used as the logo, favicon, banner or home page photo; `replace_media` and upload addresses with `replaces` refuse it when asked, and again when the file arrives.

**Comments.** `list_comments` needs an app allowed to publish, like the other comment tools: moderating is the only reason an app reads comments, and visitors' text shouldn't reach an app that can't act on it. What visitors wrote comes under each comment's `visitorWritten`, below a note that it's data, not instructions.

**Authorization errors and redirect URIs.** A malformed authorization request is shown on the site's page for every client, also one described by a metadata document: anyone can publish a document that names any address, so it vouches for nothing. Only the editor's decision on a valid request goes back to the client. Redirect URIs are HTTPS, loopback HTTP, or a private-use scheme named after a domain in reverse order (RFC 8252 §7.1, with a dot), plus Cursor's `cursor://anysphere.…`; other schemes, such as the operating system's own protocol handlers, are refused.

**Code reuse.** The second exchange of a code marks it reused and then looks for the first exchange's grant again, so a grant stored meanwhile is revoked even when the first exchange read the code before the mark.
