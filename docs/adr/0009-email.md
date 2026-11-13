# 0009: Email through Azure Communication Services

- Status: accepted (maintainer, 2026-11-13)
- Date: 2026-11-13
- Tracker: T-0130

## Context and problem statement

An admin adds an editor on the Editors page by email address, and nothing tells that person: the admin has to send the sign-in link themselves (maintainer, 2026-11-13). Weesite sends no email at all today, so this needs a way to send mail first, before release 1.0.

What it has to fit:

- the deployment is Azure Container Apps with managed identities and OpenTofu, and secrets are avoided where Entra ID works ([ADR 0001](0001-baseline.md), [ADR 0004](0004-sign-in.md));
- prod has its own domain, in an Azure DNS zone that `infra/app` manages (T-0057); test has none;
- the domain may already receive mail elsewhere (Microsoft 365 or another host, `PROD_DNS_RECORDS`), so sending mustn't take over its SPF, DKIM or DMARC records;
- the apply identity may grant only a short list of data roles, and only to managed identities (bootstrap); anything else needs a bootstrap apply by the maintainer;
- the volume is tiny: a few invitations a year.

## Considered options

1. **Azure Communication Services Email** (ACS): an Email Communication Service with a sender domain, connected to a Communication Service the app calls.
2. **An SMTP relay or a mail API** (SendGrid, Mailgun, Postmark): an account outside Azure and an API key in Key Vault.
3. **No email:** the Editors page shows a message to copy and send yourself.

## Decision

Option 1, as the maintainer chose (2026-11-13). It's in the same subscription and region as the rest, OpenTofu manages it, the app authenticates with its managed identity, and the cost at this volume is a fraction of a cent a month.

| Part | Decision |
| --- | --- |
| Resources (`infra/app/email.tf`) | An Email Communication Service and a Communication Service, data location Europe, linked by the sender domain. In every environment |
| Sender domain, test | Azure-managed (`<random>.azurecomm.net`): no DNS, ready at once; the sender is `DoNotReply@<that domain>` |
| Sender domain, prod | A subdomain of the site's domain, `mail.<domain>` by default (`var.email_subdomain`), customer-managed. Its verification TXT, SPF and DKIM records go in the Azure DNS zone under that subdomain, so the domain's own mail records stay as they are. Verification is started by an `azapi` action after the records exist. The sender is `noreply@mail.<domain>`, shown with the site's name |
| How the app sends | The `Azure.Communication.Email` client with the site's managed identity, no connection string. The identity needs a role on the Communication Service. The built-in "Communication and Email Service Owner" is too broad for the app (it lists and regenerates the access keys and deletes the services), so the bootstrap defines a custom "Weesite email sender" role with only what sending needs (reading the Communication Service and sending through it, per Microsoft's Entra ID guide) and adds it to the delegable roles: a bootstrap apply by the maintainer, once |
| Configuration | `Email:Endpoint` (the Communication Service) and `Email:Sender`, set by `infra/app`. Without them (locally, tests, a fork that hasn't applied yet) nothing is sent and the page says so |
| What sends mail | One thing: an invitation when an admin adds an editor, or changes an editor's role. Nothing to visitors, no notifications |
| The invitation | Plain text with an HTML alternative: who added them, the role, the site's name and the sign-in link, and that they sign in with Google or Microsoft using that address. In English, as the admin is |
| Failure | Best effort: adding the editor never waits for or depends on the mail. The Editors page says whether it was sent ("Invitation sent", or "couldn't be sent: tell them yourself", with the link to copy). Audited as `editor-invited` or `editor-invite-failed` |
| Tests | A fake sender records the mail; the ACS client isn't called |

## Consequences

- A bootstrap apply adds the role to the delegable list before the first `infra/app` apply with email. Without it, that apply fails at the role assignment.
- Prod's first apply creates the records and starts the verification; ACS can take up to a few hours to mark the domain verified, and sending fails until then (the page says so).
- The editor's email address is sent to ACS, a Microsoft processor in the EU data location. The privacy policy already covers editors' addresses being stored; it should say they're also used to send the invitation.
- Prod's deliverability depends on the subdomain's SPF and DKIM; DMARC stays the domain's own (`_dmarc` at the root applies to subdomains unless they have their own).
- Cost: ACS Email charges per message and per megabyte, a fraction of a cent for a handful of invitations. The Communication Service itself has no fixed cost.
- Adding other mail later (a sign-in alert, a failed backup) reuses the same sender; each needs its own decision.

## More information

- [Azure Communication Services Email overview](https://learn.microsoft.com/azure/communication-services/concepts/email/email-overview)
- [Add a custom verified email domain](https://learn.microsoft.com/azure/communication-services/quickstarts/email/add-custom-verified-domains)
- [Authenticate to ACS with Microsoft Entra ID](https://learn.microsoft.com/azure/communication-services/concepts/authentication)
