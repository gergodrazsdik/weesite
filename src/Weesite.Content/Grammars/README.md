# Grammars

TextMate grammars that [TextMateSharp](https://github.com/danipen/TextMateSharp) doesn't ship, loaded by `CodeHighlighter` next to its built-in ones (`RegistryOptions.LoadFromLocalDir`). Each folder is laid out like a VS Code extension: a `package.json` naming the languages, aliases and grammar files, the grammar under `syntaxes/`, and the upstream licence. The full list of languages, built-in and added, is in [docs/grammars.md](../../../docs/grammars.md).

| Folder | Languages (fence names) | Source | Commit | Licence |
| --- | --- | --- | --- | --- |
| `hcl` | `hcl`; `terraform`, `tf`, `tofu`, `opentofu` | [hashicorp/syntax](https://github.com/hashicorp/syntax) `syntaxes/hcl.tmGrammar.json`, `syntaxes/terraform.tmGrammar.json` | `2ec7cd10c9d6134e836bf85eb0f5aa010512bff4` (hcl), `cc2b4d4fe389f14b8a13937f4e0d7b2811b57588` (terraform) | MPL-2.0 |
| `rego` | `rego` | [open-policy-agent/vscode-opa](https://github.com/open-policy-agent/vscode-opa) `syntaxes/Rego.tmLanguage` | `9291e7c81d3656b1005e5a323834d837a321f979` | Apache-2.0 |
| `haskell` | `haskell`, `hs` | [JustusAdam/language-haskell](https://github.com/JustusAdam/language-haskell) `syntaxes/haskell.YAML-tmLanguage` | `7d3aa4811a6341767be77945a8cf5b20ad27ec64` | BSD-3-Clause |
| `just` | `just`, `justfile` | [nefrob/vscode-just](https://github.com/nefrob/vscode-just) `syntaxes/just.tmLanguage.json` | `2253973135a5cdc34e44df876fecd30aff605150` | MIT |
| `protobuf` | `proto`, `protobuf`, `proto3` | [zxh0/vscode-proto3](https://github.com/zxh0/vscode-proto3) `syntaxes/proto3.tmLanguage.json` (archived repository) | `274be763ecae7055505354b44f4892a2669e1da8` | MIT |
| `bicep` | `bicep` | [Azure/bicep](https://github.com/Azure/bicep) `src/textmate/bicep.tmlanguage` | `6252abb94141fc6e774a53652404be30f1cb9000` | MIT |
| `toml` | `toml` | [tamasfe/taplo](https://github.com/tamasfe/taplo) `editors/vscode/toml.tmLanguage.json` | `1c96904b014d2bc9fe998eae2c7d17c7c6fe204a` | MIT |

Files keep their upstream content; a missing final newline is added and the Bicep licence is dedented (`.editorconfig`). TextMateSharp reads JSON grammars only, so three were converted without changing their content:

- Rego and Bicep (plist): `python3 -I -c "import plistlib,json,sys; json.dump(plistlib.load(open(sys.argv[1],'rb')), open(sys.argv[2],'w'), indent=2, ensure_ascii=False)" Rego.tmLanguage rego.tmLanguage.json`
- Haskell (YAML): `yq -o=json haskell.YAML-tmLanguage > haskell.tmLanguage.json`

## Updating or adding a grammar

1. Download the grammar and its licence from a commit at least two weeks old, and scan them (`vscan`).
2. Convert it to JSON if needed, put it under `<folder>/syntaxes/`, and copy the licence to `<folder>/LICENSE`.
3. Add or update `<folder>/package.json` (`contributes.languages` with the fence aliases, `contributes.grammars` with the scope name and path).
4. Update the table above, `NOTICE` and [docs/grammars.md](../../../docs/grammars.md), and add a highlighting test.
