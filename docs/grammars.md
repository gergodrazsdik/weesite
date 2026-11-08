# Code highlighting grammars

Fenced code blocks are highlighted on the server by [TextMateSharp](https://github.com/danipen/TextMateSharp) (ADR 0002). The first word of a fence's info string picks the grammar by language id or alias, ignoring case; anything after it is ignored. Backtick and tilde fences both work:

````markdown
```hcl
name = "example"
```
````

A fence with an unknown or missing language renders as plain, escaped code.

This page lists every grammar the site can use: the ones TextMateSharp ships and the ones this repo adds. To add one, see [Adding a grammar](#adding-a-grammar).

## Added in this repo

Pinned files under [`src/Weesite.Content/Grammars/`](../src/Weesite.Content/Grammars/README.md), copied next to the app and loaded at startup.

| Language | Fence names | File extensions | Scope |
| --- | --- | --- | --- |
| Bicep | `bicep` | `.bicep` `.bicepparam` | `source.bicep` |
| Haskell | `haskell`, `hs` | `.hs` | `source.haskell` |
| HCL | `hcl` | `.hcl` | `source.hcl` |
| Just | `just`, `justfile` | `.just` `.justfile` | `source.just` |
| Protocol Buffers | `proto`, `protobuf`, `proto3` | `.proto` | `source.proto` |
| Rego | `rego` | `.rego` | `source.rego` |
| Terraform | `terraform`, `tf`, `tofu`, `opentofu` | `.tf` `.tofu` | `source.hcl.terraform` |
| TOML | `toml` | `.toml` | `source.toml` |

| Grammar | Source | Licence |
| --- | --- | --- |
| HCL, Terraform | [hashicorp/syntax](https://github.com/hashicorp/syntax) | MPL-2.0 |
| Rego | [open-policy-agent/vscode-opa](https://github.com/open-policy-agent/vscode-opa) | Apache-2.0 |
| Haskell | [JustusAdam/language-haskell](https://github.com/JustusAdam/language-haskell) | BSD-3-Clause |
| Just | [nefrob/vscode-just](https://github.com/nefrob/vscode-just) | MIT |
| Protocol Buffers | [zxh0/vscode-proto3](https://github.com/zxh0/vscode-proto3) (archived; [DrBlury/protobuf-vsc-extension](https://github.com/DrBlury/protobuf-vsc-extension) is a maintained alternative) | MIT |
| Bicep | [Azure/bicep](https://github.com/Azure/bicep) `src/textmate/` | MIT |
| TOML | [tamasfe/taplo](https://github.com/tamasfe/taplo) `editors/vscode/` | MIT |

The pinned commits and the conversion steps are in the [grammars README](../src/Weesite.Content/Grammars/README.md).

## Built in (TextMateSharp.Grammars 2.0.4)

Embedded in the [TextMateSharp.Grammars](https://www.nuget.org/packages/TextMateSharp.Grammars/2.0.4) package (MIT; built from commit `622d1b240a5be474939857c0b2780249022f14a3`). Each grammar's files, `package.json` and upstream licence are in the [package source](https://github.com/danipen/TextMateSharp/tree/622d1b240a5be474939857c0b2780249022f14a3/src/TextMateSharp.Grammars/Resources/Grammars); most are VS Code's built-in grammars ([microsoft/vscode](https://github.com/microsoft/vscode/tree/main/extensions), MIT, each from its own upstream). `cs`, `c#`, `yml`, `console`, `shell` and `pwsh` are extra fence names Weesite maps itself. The extension column shows at most six.

| Language | Fence names | File extensions | Scope |
| --- | --- | --- | --- |
| Asciidoc | `asciidoc`, `asciidoctor` | `.adoc` | `document.adoc` |
| Batch | `bat`, `batch` | `.bat` `.cmd` | `source.batchfile` |
| BibTeX | `bibtex` | `.bib` | `text.bibtex` |
| C | `c` | `.c` `.i` | `source.c` |
| Clojure | `clojure` | `.clj` `.cljs` `.cljc` `.cljx` `.clojure` `.edn` | `source.clojure` |
| CoffeeScript | `coffeescript`, `coffee` | `.coffee` `.cson` `.iced` | `source.coffee` |
| C++ | `cpp`, `c++` | `.cpp` `.cc` `.cxx` `.c++` `.hpp` `.hh` | `source.cpp` |
| C# | `csharp`, `c#`, `cs` | `.cs` `.csx` `.cake` | `source.cs` |
| CSS | `css` | `.css` | `source.css` |
| CUDA C++ | `cuda-cpp` | `.cu` `.cuh` | `source.cuda-cpp` |
| Dart | `dart` | `.dart` | `source.dart` |
| Diff | `diff` | `.diff` `.patch` `.rej` | `source.diff` |
| Compose | `dockercompose`, `compose` | – | `source.yaml` |
| Docker | `dockerfile`, `docker`, `containerfile` | `.dockerfile` `.containerfile` | `source.dockerfile` |
| F# | `fsharp`, `f#` | `.fs` `.fsi` `.fsx` `.fsscript` | `source.fsharp` |
| Go | `go` | `.go` | `source.go` |
| Groovy | `groovy` | `.groovy` `.gvy` `.gradle` `.jenkinsfile` `.nf` | `source.groovy` |
| Handlebars | `handlebars` | `.handlebars` `.hbs` `.hjs` | `text.html.handlebars` |
| HLSL | `hlsl` | `.hlsl` `.hlsli` `.fx` `.fxh` `.vsh` `.psh` | `source.hlsl` |
| HTML | `html`, `htm`, `xhtml` | `.html` `.htm` `.shtml` `.xhtml` `.xht` `.mdoc` | `text.html.derivative` |
| Ignore | `ignore` | `.gitignore_global` `.gitignore` | `source.ignore` |
| Ini | `ini` | `.ini` | `source.ini` |
| Pug | `jade`, `pug` | `.pug` `.jade` | `text.pug` |
| Java | `java` | `.java` `.jav` | `source.java` |
| JavaScript | `javascript`, `js` | `.js` `.es6` `.mjs` `.cjs` `.pac` | `source.js` |
| JavaScript React | `javascriptreact`, `jsx` | `.jsx` | `source.js.jsx` |
| JSON | `json` | `.json` `.bowerrc` `.jscsrc` `.webmanifest` `.js.map` `.css.map` | `source.json` |
| JSON with Comments | `jsonc` | `.jsonc` `.eslintrc` `.eslintrc.json` `.jsfmtrc` `.jshintrc` `.swcrc` | `source.json.comments` |
| Julia | `julia` | `.jl` | `source.julia` |
| LaTeX | `latex` | `.tex` `.ltx` `.ctx` | `text.tex.latex` |
| Less | `less` | `.less` | `source.css.less` |
| Log | `log` | `.log` `*.log.?` | `text.log` |
| Lua | `lua` | `.lua` | `source.lua` |
| Makefile | `makefile` | `.mak` `.mk` | `source.makefile` |
| Markdown | `markdown` | `.md` `.mkd` `.mdwn` `.mdown` `.markdown` `.markdn` | `text.html.markdown` |
| Objective-C | `objective-c` | `.m` | `source.objc` |
| Objective-C++ | `objective-cpp`, `objective-c++` | `.mm` | `source.objcpp` |
| Pascal | `pascal` | `.pas` `.pp` | `source.pascal` |
| Perl | `perl` | `.pl` `.pm` `.pod` `.t` `.PL` `.psgi` | `source.perl` |
| Perl 6 | `perl6` | `.p6` `.pl6` `.pm6` `.nqp` | `source.perl.6` |
| PHP | `php` | `.php` `.php4` `.php5` `.phtml` `.ctp` | `source.php` |
| PowerShell | `powershell`, `ps`, `ps1`, `pwsh` | `.ps1` `.psm1` `.psd1` `.pssc` `.psrc` | `source.powershell` |
| Properties | `properties` | `.properties` `.cfg` `.conf` `.directory` `.gitattributes` `.gitconfig` | `source.ini` |
| Python | `python`, `py` | `.py` `.rpy` `.pyw` `.cpy` `.gyp` `.gypi` | `source.python` |
| R | `r` | `.r` `.rhistory` `.rprofile` `.rt` | `source.r` |
| Razor | `razor` | `.cshtml` `.razor` | `text.html.cshtml` |
| Ruby | `ruby`, `rb` | `.rb` `.rbx` `.rjs` `.gemspec` `.rake` `.ru` | `source.ruby` |
| Rust | `rust` | `.rs` | `source.rust` |
| SCSS | `scss` | `.scss` | `source.css.scss` |
| ShaderLab | `shaderlab` | `.shader` | `source.shaderlab` |
| Shell Script | `shellscript`, `bash`, `sh`, `zsh`, `ksh`, `csh`, `console`, `shell` | `.sh` `.bash` `.bashrc` `.bash_aliases` `.bash_profile` `.bash_login` | `source.shell` |
| SQL | `sql` | `.sql` `.dsql` | `source.sql` |
| Swift | `swift` | `.swift` | `source.swift` |
| TeX | `tex` | `.sty` `.cls` `.bbx` `.cbx` | `text.tex` |
| TypeScript | `typescript`, `ts` | `.ts` | `source.ts` |
| TypeScript React | `typescriptreact`, `tsx` | `.tsx` | `source.tsx` |
| Typst | `typst`, `typ` | `.typ` | `source.typst` |
| Typst (Code Mode) | `typst-code`, `typc` | `.typc` | `source.typst-code` |
| Visual Basic | `vb` | `.vb` `.brs` `.vbs` `.bas` | `source.asp.vb.net` |
| x86 Assembly | `x86asm`, `x86`, `assembly` | `.asm` | `source.x86asm` |
| XML | `xml` | `.xml` `.xsd` `.ascx` `.atom` `.axml` `.axaml` | `text.xml` |
| XSL | `xsl` | `.xsl` `.xslt` | `text.xml.xsl` |
| YAML | `yaml`, `yml` | `.yml` `.eyaml` `.eyml` `.yaml` | `source.yaml` |

## Not available yet

Languages a technical blog may want that neither list covers, with no grammar picked yet: KQL, Nix, Kotlin, Zig, Elixir, GraphQL. Most have a VS Code extension with a TextMate grammar; check its licence (permissive or MPL-2.0) before adding it.

## Adding a grammar

1. Find the grammar (`*.tmLanguage.json`, `*.tmGrammar.json`, or a plist or YAML grammar to convert) in a maintained repository with a compatible licence, and pin a commit at least two weeks old.
2. Follow the steps in the [grammars README](../src/Weesite.Content/Grammars/README.md#updating-or-adding-a-grammar): files and licence under `src/Weesite.Content/Grammars/<folder>/`, a `package.json` with the fence aliases, the table there, `NOTICE`, this page, and a test in `MarkdownRendererTests`.
3. If its scopes use names outside the usual TextMate conventions, check that `CodeHighlighter` maps them to `hl-*` classes (the test shows it).
