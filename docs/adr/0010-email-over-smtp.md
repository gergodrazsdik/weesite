# 0010: Email over SMTP, with any provider

- Status: accepted (maintainer, 2026-11-13)
- Date: 2026-11-13
- Tracker: T-0130, Q-0008
- Supersedes: [ADR 0009](0009-email.md)

## Context and problem statement

[ADR 0009](0009-email.md) chose Azure Communication Services Email to send the one mail Weesite needs: an invitation when an admin adds an editor. Before anything was built, Microsoft's [retirement guide](https://learn.microsoft.com/azure/communication-services/acs-retirement-and-breaking-changes-guide) turned out to list ACS Email as retired on 2028-09-30, in maintenance mode, with new resources possible only "subject to change at any time", and Microsoft advising against onboarding new workloads (Q-0008).

The rest of ADR 0009's context still holds: tiny volume, a domain that may already receive mail elsewhere, and secrets avoided where possible. It adds one requirement: Weesite is an engine others deploy, so it shouldn't tie every deployment to one provider.

## Considered options

1. **ACS Email until 2028**, as ADR 0009 decided: a managed identity and no secret, but a forced migration, and new deployments could be refused at any time.
2. **SMTP submission to any provider:** Microsoft 365 High Volume Email, Postmark, Mailgun, Brevo, Amazon SES and others all accept it. The cost is one password per environment.
3. **Microsoft 365 through Microsoft Graph** with the managed identity: no secret, but it needs a licensed mailbox and Exchange application access set up by hand, and deployments without Microsoft 365 get no email.
4. **No email:** only an invitation to copy.

## Decision outcome

Chosen option: 2, SMTP with any provider, as the maintainer chose (2026-11-13). Every deployment picks its own provider, and nothing depends on a retiring service. Option 4 is part of it anyway: the invitation can always be copied.

| Part | Decision |
| --- | --- |
| Client | [MailKit](https://github.com/jstedfast/MailKit) (MIT). .NET's own `SmtpClient` isn't recommended for new code |
| Connection | `Email:Smtp:Host`, `Email:Smtp:Port` (587 by default), `Email:Smtp:Security`: `StartTls` (default) or `SslOnConnect` (port 465). Never without TLS |
| Credentials | `Email:Smtp:UserName` and `Email:Smtp:Password`, in the environment's Key Vault, loaded at startup like the sign-in secrets ([ADR 0004](0004-sign-in.md)). `just smtp <env>` stores them, the host, port and sender. No Azure resources and no `infra/app` change |
| Sender | `Email:From`, an address the provider allows. The display name is the site's name |
| What sends mail | One thing: an invitation when an admin adds an editor, or changes an editor's role. Nothing to visitors, no notifications |
| The invitation | Plain text with an HTML alternative: who added them, the role, the site's name, the sign-in link, and that they sign in with Google or Microsoft using that address. In English, as the admin is |
| Sending | After the editor is saved, within the same request, with a 15-second timeout. The editor is added whatever happens to the mail |
| On the Editors page | "Invitation sent", "couldn't be sent", or, without settings, nothing about sending. In every case, the invitation is there to copy |
| Audit | `editor-invited` or `editor-invite-failed` (with the reason, not the message) |
| Tests | A fake sender records the mail; the MailKit sender is tested on the message it builds |

### Consequences

- One secret per environment, the SMTP password. Rotating it is the maintainer's job, with the provider, then `just smtp <env>` and a restart.
- Deliverability depends on the provider's SPF and DKIM records for the sender's domain. On prod they go in `PROD_DNS_RECORDS` like any other record, at a subdomain if the domain already sends mail elsewhere.
- Editors' addresses go to the chosen provider, a processor. The privacy policy should say so, and name the provider.
- The admin's request waits for the mail, at most 15 seconds.
- The bootstrap needs no change: #244 (ACS's sender role) isn't needed.

## More information

- [ACS retirement and breaking changes guide](https://learn.microsoft.com/azure/communication-services/acs-retirement-and-breaking-changes-guide)
- [MailKit](https://github.com/jstedfast/MailKit)
