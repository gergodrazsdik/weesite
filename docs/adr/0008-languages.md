# 0008: Sites in any language, and items in another language

- Status: proposed
- Date: 2026-11-12
- Tracker: T-0060, F-0072

## Context and problem statement

The brief planned Hungarian as a second site language: English at the root, Hungarian under `/hu/`, translations linked through a translation group, `hreflang` alternates, per-language feeds and `llms.txt` sections, and one FTS5 table per language ([brief, Localization](../design/brief.md#application-stack); [ADR 0002](0002-public-site.md), URL scheme).

The maintainer doesn't want a bilingual site: there will be a few posts in Hungarian, but no post written twice (2026-11-12). Two needs remain:

- **A site in another language:** someone who forks Weesite should be able to run it in German or Hungarian, with the public site's text, dates and metadata in that language.
- **An item in another language:** an English site with a few Hungarian posts should mark them as Hungarian, for screen readers, search engines, feeds and search.

What the code does today:

- the site's `Language` setting sets `<html lang>`, the feeds' language and JSON-LD `inLanguage`;
- the request culture is fixed to `en`, so month names and the lists' short dates stay English whatever the setting;
- there is no `og:locale`;
- keyword search uses FTS5's `porter` stemmer, which is English-only, and the embedding model (`bge-small-en-v1.5`) is English-only;
- all 507 UI strings are in one resource file, admin and public mixed;
- items have a `lang` field, but the editor overwrites it with the site's language on every save (F-0072);
- the `translation` field is read and written but used nowhere, and the editor drops it on save.

## Considered options

1. **The brief's bilingual site:** `/hu/` routes, translation groups, `hreflang`, per-language feeds, settings and menus.
2. **One site language, any language, plus a language per item:** the site's language drives the culture and the public text; an item in another language is marked as such, in the site's lists, feeds and search like any other.
3. **Leave it as it is:** English sites only, `lang` as a field nobody reads.

## Decision outcome

Chosen option: 2. Option 1 is a lot of routing, settings and editor work for a use the maintainer doesn't have, and a fork that wants it can build it on option 2. Option 3 leaves forks with an English-only site and Hungarian posts marked as English.

### The site's language

- The request culture follows the site's `Language` (any .NET culture name, such as `hu` or `de-AT`): month and day names in the date format, and the lists' short dates (today fixed to `MMM dd`) take the culture's own short form.
- `og:locale` from the language, as language and territory (`hu` gives `hu_HU`), and `dir="rtl"` on `<html>` for right-to-left languages. The stylesheets already use logical properties.
- The UI strings split in two: `PublicResource` (the public site, about a third of the strings) and `AdminResource` (everything under `/admin`). A fork translates the public file and can leave the admin in English. A language without a file falls back to English.
- Weesite ships Hungarian public strings, as the worked example and the test that the plumbing works. `docs/` explains how to add a language.

### An item's language

- An item's `lang` (front matter) marks its title, text and tags: the post's `<article>`, and its title in lists, get `lang` when it differs from the site's. Screen readers switch pronunciation; browsers hyphenate and spell-check in that language.
- That item's page gets its own `og:locale`; JSON-LD and JSON Feed already give the item's language, and Atom entries get `xml:lang`.
- No language label in lists: the title tells readers (maintainer, 2026-11-12). No `hreflang`, since there are no translations.
- The editor gets a Language field, defaulting to the site's language, and keeps the item's language on save (F-0072). The MCP tools already take and return `lang` in the front matter; their descriptions say so.
- The `translation` field goes: nothing uses it. Files that still have it load as before.
- `hu` is no longer a reserved slug, since there is no `/hu/` route.

### Search

- Keyword search picks its tokenizer by each item's language: `porter` for English, `unicode61 remove_diacritics 2` for others, and queries are matched against both.
- The embedding model becomes a deployment setting, `Search:EmbeddingModel`: `bge-small-en-v1.5` (the default) or `multilingual-e5-small`. The image carries both, pinned and checked by SHA-256 like today. e5 needs a SentencePiece tokenizer and the `query:` and `passage:` prefixes; both models make 384-dimension vectors, and the stored vectors name their model, so switching re-embeds.
- Test runs `multilingual-e5-small` to try it, and prod stays on `bge-small-en-v1.5` to keep its memory low (maintainer, 2026-11-12). Test may need the 1 GiB replica for it; it scales to zero, so that costs little.

### Consequences

- Good: a fork runs its public site in its own language by translating one resource file and setting the language; mixed-language content is marked correctly everywhere it appears.
- Good: the multilingual model is tried on test before anyone pays for it on prod.
- Bad: the image grows by about 120 MB, so a cold start on test pulls a little longer (F-0063).
- Bad: two resource files to keep in step; a test checks that every key in the Hungarian file exists in the English one.
- Neutral: this supersedes the brief's Hungarian plan and the `/hu/` part of ADR 0002's URL scheme.

## More information

- To check before the search work starts: that Microsoft.ML.Tokenizers' SentencePiece tokenizer reads e5's (XLM-RoBERTa, unigram) tokenizer model; otherwise a small unigram tokenizer of our own.
- Hungarian suffixes ("Kubernetesről", "Kubernetesben") defeat keyword search without a Hungarian stemmer, so meaning search matters more for Hungarian items than for English ones.
- [Open Graph: og:locale](https://ogp.me/#optional), [HTML: the lang attribute](https://html.spec.whatwg.org/multipage/dom.html#the-lang-and-xml:lang-attributes), [SQLite FTS5 tokenizers](https://sqlite.org/fts5.html#tokenizers), [multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small).
