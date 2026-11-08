# Grammars

TextMate grammars that [TextMateSharp](https://github.com/danipen/TextMateSharp) doesn't ship, loaded by `CodeHighlighter` next to its built-in ones (`RegistryOptions.LoadFromLocalDir`). Each folder is laid out like a VS Code extension: a `package.json` naming the languages, aliases and grammar files, the grammar under `syntaxes/`, and the upstream licence. The full list of languages, built-in and added, is in [docs/grammars.md](../../../docs/grammars.md).

| Folder | Languages (fence names) | Source | Commit | Licence |
| --- | --- | --- | --- | --- |
| `hcl` | `hcl`; `terraform`, `tf`, `tofu`, `opentofu` | [hashicorp/syntax](https://github.com/hashicorp/syntax) `syntaxes/hcl.tmGrammar.json`, `syntaxes/terraform.tmGrammar.json` | `2ec7cd10c9d6134e836bf85eb0f5aa010512bff4` (hcl), `cc2b4d4fe389f14b8a13937f4e0d7b2811b57588` (terraform) | MPL-2.0 |
| `rego` | `rego` | [open-policy-agent/vscode-opa](https://github.com/open-policy-agent/vscode-opa) `syntaxes/Rego.tmLanguage` | `9291e7c81d3656b1005e5a323834d837a321f979` | Apache-2.0 |
| `haskell` | `haskell`, `hs` | [JustusAdam/language-haskell](https://github.com/JustusAdam/language-haskell) `syntaxes/haskell.YAML-tmLanguage` | `7d3aa4811a6341767be77945a8cf5b20ad27ec64` | BSD-3-Clause |

TextMateSharp reads JSON grammars only, so two were converted without changing their content:

- Rego (plist): `python3 -I -c "import plistlib,json,sys; json.dump(plistlib.load(open(sys.argv[1],'rb')), open(sys.argv[2],'w'), indent=2, ensure_ascii=False)" Rego.tmLanguage rego.tmLanguage.json`
- Haskell (YAML): `yq -o=json haskell.YAML-tmLanguage > haskell.tmLanguage.json`

## Updating or adding a grammar

1. Download the grammar and its licence from a commit at least two weeks old, and scan them (`vscan`).
2. Convert it to JSON if needed, put it under `<folder>/syntaxes/`, and copy the licence to `<folder>/LICENSE`.
3. Add or update `<folder>/package.json` (`contributes.languages` with the fence aliases, `contributes.grammars` with the scope name and path).
4. Update the table above, `NOTICE` and [docs/grammars.md](../../../docs/grammars.md), and add a highlighting test.
