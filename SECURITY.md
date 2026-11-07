# Security policy

## Reporting a vulnerability

Please report vulnerabilities through **GitHub private vulnerability reporting**: on the repository's **Security** tab, choose **Report a vulnerability** ([direct link](https://github.com/gergodrazsdik/weesite/security/advisories/new)). Do not open a public issue, pull request or discussion about it.

Include what you found, how to reproduce it, and the impact you expect.

## Scope

In scope:

- The Weesite application: the public site, the `/admin` editor and its sign-in and roles, the MCP endpoint, feeds, search, uploads and media serving, HTML export.
- The infrastructure code in `infra/` and the policy baseline in `policy/`, where a default would leave a deployment exposed.
- The repository's workflows and scripts (for example a workflow that leaks secrets or can be triggered by an outside contributor).

Out of scope:

- Problems in a specific deployment's configuration or hosting account that the defaults don't cause.
- Vulnerabilities in dependencies that are already public and tracked upstream (Dependabot picks them up), unless Weesite uses them in an exploitable way.
- Rate-limit or resource-exhaustion reports without a concrete impact beyond what any small public site faces.

## Supported versions

Only the latest release gets security fixes. Weesite is pre-alpha and has no releases yet.

## Response

Weesite has a single maintainer, so responses are best effort. I aim to acknowledge a report within a week and will keep you updated while it's investigated. Fixed vulnerabilities are published as GitHub security advisories, with credit if you want it.
