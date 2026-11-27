# Code highlighting grammars

Fenced code blocks are highlighted on the server by the native highlighter ([ADR 0012](adr/0012-faster-starts.md)): [syntect](https://github.com/trishume/syntect) in a Rust library (`native/highlight`), with [bat](https://github.com/sharkdp/bat)'s Sublime syntaxes and a few of Weesite's own. The first word of a fence's info string picks the language by one of its fence names, ignoring case; anything after it is ignored. Backtick and tilde fences both work:

````markdown
```hcl
name = "example"
```
````

A fence with an unknown or missing language renders as plain, escaped code. The editor's Code block button opens a search over these languages and inserts a fence with the language's first fence name, and the editor warns when a post names a language without a grammar; `text`, `plaintext`, `plain` and `txt` ask for plain code on purpose.

## Where the grammars come from

- **bat's syntaxes**, about 190 of them, as [two-face](https://codeberg.org/CosmicHarper/two-face) 0.5.2 embeds them (bat 0.26.1). Their licences are in [`native/highlight/SYNTAX-LICENSES.md`](../native/highlight/SYNTAX-LICENSES.md).
- **Weesite's own**, for languages bat's set lacks or has an older grammar of: the TextMate grammars Weesite highlighted them with before, converted to Sublime syntaxes by `scripts/convert-grammars.py`. Each is in a folder of [`native/highlight/grammars/`](../native/highlight/grammars/) with its source, licence and `meta.json`.

| Folder | Language | Source | Licence |
| --- | --- | --- | --- |
| `bicep` | Bicep | [Azure/bicep](https://github.com/Azure/bicep) `src/textmate/bicep.tmlanguage` at `6252abb9` | MIT |
| `handlebars` | Handlebars | [daaain/Handlebars](https://github.com/daaain/Handlebars) at `85a153a6`, as VS Code ships it | MIT |
| `hlsl` | HLSL | [tgjones/shaders-tmLanguage](https://github.com/tgjones/shaders-tmLanguage) at `87c0dca3`, as VS Code ships it | MIT |
| `just` | Just | [nefrob/vscode-just](https://github.com/nefrob/vscode-just) `syntaxes/just.tmLanguage.json` at `22539731` | MIT |
| `perl6` | Perl 6 | [textmate/perl.tmbundle](https://github.com/textmate/perl.tmbundle) at `a85927a9`, as VS Code ships it | TextMate Bundle License |
| `pug` | Pug | [davidrios/pug-tmbundle](https://github.com/davidrios/pug-tmbundle) at `e67e895f`, as VS Code ships it | MIT |
| `razor` | Razor | [dotnet/razor](https://github.com/dotnet/razor) at `9b1e979b`, as VS Code ships it | MIT |
| `rego` | Rego | [open-policy-agent/vscode-opa](https://github.com/open-policy-agent/vscode-opa) `syntaxes/Rego.tmLanguage` at `9291e7c8`: newer than bat's, with Rego v1's `if` and `contains` | Apache-2.0 |
| `shaderlab` | ShaderLab | [tgjones/shaders-tmLanguage](https://github.com/tgjones/shaders-tmLanguage) at `c72c8b39`, as VS Code ships it | MIT |
| `vb` | Visual Basic | [textmate/asp.vb.net.tmbundle](https://github.com/textmate/asp.vb.net.tmbundle) at `72d44550`, as VS Code ships it | TextMate Bundle License |

**Fence names** are Weesite's own where it had the language before the switch (`native/highlight/src/languages.rs`): the first is the one the editor inserts, and every fence name Weesite took before still picks its language. A few languages that had a grammar of their own before now use a close one: Compose files YAML, JSON with Comments JSON, CUDA C++ C++, plain HCL Terraform's, and Typst's code mode Typst. Other syntaxes answer to their file extensions and, when it's one word, their name.

## Languages

Generated from the library (`cargo run --example languages -- --write ../../docs/grammars.md` in `native/highlight`); the build job checks it's current.

<!-- languages:begin -->

| Language | Fence names |
| --- | --- |
| ActionScript | `actionscript`, `as` |
| Ada | `ada`, `adb`, `ads`, `gpr` |
| Apache Conf | `envvars`, `htaccess`, `htgroups`, `htpasswd` |
| AppleScript | `applescript` |
| ARM Assembly | `s` |
| Asciidoc | `asciidoc`, `asciidoctor`, `adoc`, `ad` |
| ASP | `asa` |
| Authorized Keys | `authorized_keys`, `authorized_keys2`, `pub` |
| AWK | `awk` |
| Batch | `bat`, `batch`, `cmd` |
| BibTeX | `bibtex`, `bib` |
| Bicep | `bicep`, `bicepparam` |
| C | `c`, `h` |
| C# | `csharp`, `c#`, `cs`, `csx` |
| C++ | `cpp`, `c++`, `cuda-cpp`, `cc`, `cxx`, `hpp`, `cp`, `h++`, `hh`, `hxx`, `inl`, `ipp` |
| Cabal | `cabal` |
| camlp4 | `camlp4` |
| CFML | `cfc`, `cfm`, `cfml` |
| Clojure | `clojure`, `clj`, `cljc`, `cljs`, `edn` |
| CMake | `cmake`, `cmakelists.txt` |
| CMake C Header | `h.in` |
| CMake C++ Header | `h++.in`, `hh.in`, `hpp.in`, `hxx.in` |
| CMakeCache | `cmakecache`, `cmakecache.txt` |
| CoffeeScript | `coffeescript`, `coffee`, `cakefile`, `coffee.erb`, `cson` |
| Command Help | `cmd-help`, `help` |
| CpuInfo | `cpuinfo` |
| Crontab | `cron.d`, `crontab`, `tab` |
| Crystal | `cr`, `crystal` |
| CSS | `css`, `css.erb`, `css.liquid` |
| D | `d`, `di` |
| Dart | `dart` |
| debsources | `debsources`, `sources.list` |
| Diff | `diff`, `patch` |
| Docker | `dockerfile`, `docker`, `containerfile` |
| DotENV | `dotenv`, `env`, `env.example`, `env.sample`, `env.template` |
| Elixir | `elixir`, `ex`, `exs` |
| Elm | `elm` |
| Email | `email`, `eml`, `mboxz`, `mbx`, `msg` |
| Erlang | `emakefile`, `erl`, `erlang`, `escript`, `hrl` |
| F# | `fsharp`, `f#`, `fs`, `fsi`, `fsx` |
| Fish | `fish` |
| Fortran (Fixed Form) | `f`, `f77`, `for`, `fpp` |
| Fortran (Modern) | `f03`, `f08`, `f90`, `f95` |
| Fortran Namelist | `namelist` |
| fstab | `crypttab`, `fstab`, `mtab` |
| GDScript (Godot Engine) | `gd` |
| Git Attributes | `attributes`, `gitattributes` |
| Git Commit | `commit_editmsg`, `merge_msg`, `tag_editmsg` |
| Git Config | `gitconfig` |
| Git Log | `gitlog` |
| Git Mailmap | `mailmap` |
| Git Rebase Todo | `git-rebase-todo` |
| GLSL | `comp`, `frag`, `fsh`, `fshader`, `geom`, `glsl`, `gs`, `gsh`, `gshader`, `mesh`, `rahit`, `rcall`, `rchit`, `rgen`, `rint`, `rmiss`, `task`, `tesc`, `tese`, `vert`, `vs`, `vshader` |
| gnuplot | `gnu`, `gnuplot`, `gp`, `gpl`, `plot`, `plt` |
| Go | `go` |
| Gomod | `go.mod`, `go.work`, `gomod` |
| Gosum | `go.sum`, `gosum` |
| GraphQL | `gql`, `graphql`, `graphqls` |
| Graphviz (DOT) | `dot`, `gv` |
| Groff/troff | `1`, `2`, `3`, `4`, `5`, `6`, `7`, `8`, `9`, `groff`, `troff` |
| Groovy | `groovy`, `gradle`, `gvy`, `jenkinsfile` |
| group | `group` |
| Handlebars | `handlebars`, `hbs` |
| Haskell | `haskell`, `hs` |
| Highlight non-printables | `show-nonprintable` |
| HLSL | `hlsl`, `cginc`, `compute`, `fx`, `fxh`, `hlsli`, `psh`, `vsh` |
| Hosts File | `hosts` |
| HTML | `html`, `htm`, `xhtml`, `shtml` |
| HTML (ASP) | `asp` |
| HTML (EEx) | `html.eex`, `html.leex` |
| HTML (Erlang) | `yaws` |
| HTML (Jinja2) | `htm.j2`, `html.j2`, `xhtml.j2`, `xml.j2` |
| HTML (Rails) | `erb`, `html.erb`, `rails`, `rhtml` |
| HTML (Tcl) | `adp` |
| HTML (Twig) | `html.twig`, `twig` |
| HTTP Request and Response | `http` |
| Idris | `idr`, `idris` |
| Ignore | `ignore`, `gitignore`, `exclude` |
| Ini | `ini`, `cfg`, `desktop`, `hgrc`, `inf`, `lng`, `reg`, `url` |
| Java | `java`, `bsh` |
| Java Server Page (JSP) | `jsp` |
| JavaScript | `htc` |
| JavaScript | `javascript`, `js`, `javascriptreact`, `jsx`, `mjs`, `cjs`, `babel`, `es6` |
| JavaScript (Rails) | `js.erb` |
| Jinja2 | `j2`, `jinja`, `jinja2` |
| JQ | `jq` |
| JSON | `json`, `jsonc`, `ipynb`, `pipfile.lock`, `sublime-build`, `sublime-color-scheme`, `sublime-commands`, `sublime-completions`, `sublime-keymap`, `sublime-macro`, `sublime-menu`, `sublime-mousemap`, `sublime-project`, `sublime-settings`, `sublime-theme` |
| jsonnet | `jsonnet`, `libjsonnet`, `libsonnet` |
| Julia | `julia`, `jl` |
| Just | `just`, `justfile` |
| Known Hosts | `known_hosts`, `known_hosts.old` |
| Kotlin | `kotlin`, `kt`, `kts` |
| LaTeX | `latex`, `ltx` |
| Lean 4 | `lean` |
| Less | `less`, `css.less` |
| Lisp | `cl`, `clisp`, `el`, `fasl`, `l`, `lisp`, `lsp`, `mud`, `scm`, `sld`, `ss` |
| Literate Haskell | `lhs` |
| LiveScript | `livescript`, `ls`, `ls.erb`, `slakefile` |
| LLVM | `ll`, `llvm` |
| Log | `log` |
| Lua | `lua` |
| Makefile | `makefile`, `make`, `mk`, `gnumakefile`, `mak`, `makefile.am`, `makefile.in`, `ocamlmakefile` |
| Manpage | `man`, `manpage` |
| Markdown | `markdown`, `md`, `markdn`, `mdown` |
| MATLAB | `matlab` |
| MediaWiki | `mediawiki`, `wiki`, `wikipedia` |
| MemInfo | `meminfo` |
| MultiMarkdown | `multimarkdown` |
| NAnt Build File | `build` |
| nginx | `conf`, `conf.erb`, `fastcgi_params`, `mime.types`, `nginx`, `nginx.conf`, `scgi_params`, `uwsgi_params` |
| Nim | `nim`, `nimble`, `nims` |
| Ninja | `ninja` |
| Nix | `nix` |
| NSIS | `bnsh`, `bnsi`, `nsdinc`, `nsh`, `nsi`, `nsis` |
| Objective-C | `objective-c`, `objc`, `m` |
| Objective-C++ | `objective-cpp`, `objective-c++`, `mm` |
| OCaml | `ml`, `mli`, `ocaml` |
| OCamllex | `mll`, `ocamllex` |
| OCamlyacc | `mly`, `ocamlyacc` |
| Odin | `odin` |
| orgmode | `org`, `orgmode` |
| Pascal | `pascal`, `dpr`, `p`, `pas` |
| passwd | `passwd` |
| Perl | `perl`, `pl`, `pc`, `pm`, `pmc`, `pod`, `t` |
| Perl 6 | `perl6`, `raku`, `p6`, `nqp`, `pl6`, `pm6` |
| PHP | `php`, `php3`, `php4`, `php5`, `php7`, `phps`, `phpt`, `phtml` |
| PowerShell | `powershell`, `ps`, `ps1`, `pwsh`, `psd1`, `psm1` |
| Properties | `properties` |
| Protocol Buffer (TEXT) | `pb.txt`, `pbtxt`, `proto.text`, `prototxt`, `textpb`, `textproto` |
| Protocol Buffers | `proto`, `protobuf`, `proto3`, `protodevel` |
| Pug | `jade`, `pug` |
| Puppet | `epp`, `pp`, `puppet` |
| PureScript | `purescript`, `purs` |
| Python | `python`, `py`, `bazel`, `bzl`, `cpy`, `gyp`, `gypi`, `pxd`, `pxd.in`, `pxi`, `pxi.in`, `py3`, `pyi`, `pyw`, `pyx`, `pyx.in`, `rpy`, `sconscript`, `sconstruct`, `snakefile`, `vpy`, `wscript` |
| QML | `qml`, `qmlproject` |
| R | `r`, `rprofile` |
| Racket | `racket`, `rkt` |
| Razor | `razor`, `cshtml` |
| Rd (R Documentation) | `rd` |
| Rego | `rego` |
| Regular Expression | `re` |
| Requirements.txt | `pip`, `requirements.in`, `requirements.txt` |
| resolv | `resolv`, `resolv.conf` |
| reStructuredText | `rest`, `restructuredtext`, `rst` |
| Robot Framework | `resource`, `robot` |
| Ruby | `ruby`, `rb`, `appfile`, `appraisals`, `berksfile`, `brewfile`, `capfile`, `cgi`, `cheffile`, `config.ru`, `deliverfile`, `fastfile`, `fcgi`, `gemfile`, `gemspec`, `guardfile`, `irbrc`, `jbuilder`, `podfile`, `podspec`, `prawn`, `rabl`, `rake`, `rakefile`, `rantfile`, `rbx`, `rjs`, `ruby.rail`, `scanfile`, `simplecov`, `snapfile`, `thor`, `thorfile`, `vagrantfile` |
| Ruby Haml | `haml` |
| Ruby on Rails | `builder`, `rxml` |
| Ruby Slim | `skim`, `slim` |
| Rust | `rust`, `rs` |
| Salt State (SLS) | `sls` |
| Sass | `sass` |
| Scala | `sbt`, `sc`, `scala` |
| SCSS | `scss` |
| Separated Values | `csv` |
| ShaderLab | `shaderlab`, `shader` |
| Shell Script | `shellscript`, `bash`, `sh`, `zsh`, `ksh`, `csh`, `console`, `shell`, `ash`, `ebuild`, `eclass`, `pkgbuild` |
| SML | `cm`, `sig`, `sml` |
| Solidity | `sol`, `solidity` |
| SQL | `sql`, `ddl`, `dml` |
| SQL (Rails) | `erbsql`, `sql.erb` |
| SSH Config | `ssh_config` |
| SSHD Config | `sshd_config` |
| Strace | `strace` |
| Stylus | `styl`, `stylus` |
| Svelte | `svelte`, `svlt` |
| Swift | `swift` |
| syslog | `syslog` |
| SystemVerilog | `sv`, `svh`, `systemverilog`, `vh` |
| Tab Separated Values | `tsv` |
| Tcl | `tcl` |
| Terraform | `terraform`, `tf`, `tofu`, `opentofu`, `hcl`, `tfvars` |
| TeX | `tex`, `cls`, `sty` |
| Textile | `textile` |
| Todo.txt | `done.txt`, `todo.txt` |
| TOML | `toml`, `cargo.lock`, `gopkg.lock`, `pdm.lock`, `pipfile`, `poetry.lock`, `tml`, `uv.lock` |
| TypeScript | `typescript`, `ts`, `cts`, `mts` |
| TypeScript React | `typescriptreact`, `tsx` |
| Typst | `typst`, `typ`, `typst-code`, `typc` |
| varlink | `varlink` |
| Verilog | `v`, `verilog` |
| VHDL | `vhd`, `vhdl`, `vho`, `vht` |
| VimHelp | `vimhelp` |
| VimL | `_exrc`, `_gvimrc`, `_vimrc`, `gvimrc`, `vim`, `viml`, `vimrc` |
| Visual Basic | `vb`, `vbnet`, `bas`, `brs`, `vba`, `vbs` |
| Vue Component | `vue` |
| Vyper | `vy`, `vyper` |
| WGSL | `wgsl` |
| x86 Assembly | `x86asm`, `x86`, `assembly`, `asm`, `inc`, `mac`, `nasm`, `yasm` |
| XML | `xml`, `xsl`, `xslt`, `svg`, `dtml`, `opml`, `rng`, `rss`, `tld`, `xaml`, `xsd` |
| YAML | `yaml`, `yml`, `dockercompose`, `compose`, `sublime-syntax` |
| Zig | `zig`, `zon` |

<!-- languages:end -->

## Not available yet

Languages a technical blog may want that neither bat's set nor Weesite's grammars cover: KQL. Most have a Sublime syntax, or a VS Code extension with a TextMate grammar; check its licence (permissive or MPL-2.0) before adding it.

## Adding a grammar

1. Find the grammar in a maintained repository with a compatible licence, and pin a commit at least two weeks old. A Sublime syntax (`.sublime-syntax`) can be used as it is; a TextMate grammar (`*.tmLanguage.json`, or a plist or YAML grammar converted to JSON) is converted.
2. Make a folder under `native/highlight/grammars/` with the grammar as `source.json`, its licence as `LICENSE`, and a `meta.json` with its `name` (unique in the set), `extensions` and `source` (repository, path, commit and licence). Run `scripts/convert-grammars.py`, which writes `<folder>.sublime-syntax` beside them; for a Sublime syntax, put it there yourself and leave out `source.json`.
3. Give it Weesite's fence names in `native/highlight/src/languages.rs`, the one the editor should insert first.
4. Check how it reads some code (`cargo run --example scopes -- <fence> '<code>'`) and that its scopes give `hl-*` classes; add a case to the grammar tests in `native/highlight/src/lib.rs`.
5. Update the table above, `NOTICE`, and the list of languages (step "Languages" above).
