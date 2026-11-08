# Architecture decision records

Decisions about Weesite's architecture, infrastructure and process. The [design brief](../design/brief.md) is the starting point; ADRs record what was decided, refine it and supersede it.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-baseline.md) | Baseline decisions from the design brief | accepted |
| [0002](0002-public-site.md) | Public site choices for Phase 2 | accepted |

## Numbering and status

- Files are named `NNNN-short-title.md`: four digits, in order, never reused.
- Status is one of `proposed`, `accepted`, `superseded by NNNN` or `deprecated`.
- An accepted ADR isn't edited to change its decision: a new ADR supersedes it, and the old one's status points to the new one. Fixing typos and links is fine.
- Group related small decisions into one ADR rather than writing one file per tiny choice.
- Add a line to the tracker's **Decisions** list for every accepted ADR.

## Template

[MADR](https://adr.github.io/madr/)-style:

```markdown
# NNNN: Title

- Status: proposed | accepted | superseded by NNNN | deprecated
- Date: YYYY-MM-DD
- Tracker: T-NNNN, Q-NNNN

## Context and problem statement

## Considered options

## Decision outcome

Chosen option: "…", because …

### Consequences

## More information
```
