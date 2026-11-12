# Languages

A Weesite site has one language, which can be any language ([ADR 0008](adr/0008-languages.md)). Posts and pages can each be in another language. There's no bilingual mode: a post isn't linked to a translation of itself.

## The site's language

Set it in **Site settings → Language** (`language` in `site.json`): a code such as `en`, `en-GB`, `de` or `hu`. Every page then runs in that language's culture:

- `<html lang>`, the feeds' language and `og:locale` (`hu` gives `hu_HU`), and `dir="rtl"` for right-to-left languages;
- month and day names in the **Date format** (`dateFormat`, a .NET date format such as `yyyy. MMMM d.` or `d MMMM yyyy`);
- the short dates in the post lists, in the language's own short form: "Oct 12" in English, "okt. 12." in Hungarian;
- the public site's text, if Weesite has it in that language (below); English otherwise.

An unknown code falls back to English.

## The site's text

The UI strings are in two resource files under [`src/Weesite.Web/Resources/`](../src/Weesite.Web/Resources/):

| File | What | Languages |
| --- | --- | --- |
| `PublicResource.resx` | The public site: menus, lists, search, share, 404 and error pages, the copy button on code | English, Hungarian (`PublicResource.hu.resx`) |
| `AdminResource.resx` | Everything under `/admin`, sign-in and the MCP consent page | English |

A site in a language without its own public file shows the public site in English. The admin is in English for every site.

### Adding a language

1. Copy `PublicResource.resx` to `PublicResource.<code>.resx`, with the code you set in Site settings (`de`, `pt-BR`).
2. Translate each `<value>`. Keep the keys, and keep every placeholder (`{0}`, `{1}`) once, wherever the sentence needs it.
3. Add the code to `A_translation_has_every_public_key_with_the_same_placeholders` in [`tests/Weesite.Web.Tests/ResourceTests.cs`](../tests/Weesite.Web.Tests/ResourceTests.cs) and run `just test`: it checks that the file has every key and the same placeholders.

The admin can be translated the same way, as `AdminResource.<code>.resx`.

## Search

Keyword search uses an English stemmer, and the default embedding model is English-only. ADR 0008 plans keyword search by each item's language and a multilingual model as a deployment setting; until then, search works best on English text.
