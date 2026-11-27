//! Weesite's fence names (T-0189): which syntax each picks, and the name the editor shows for it.
//!
//! The first fence name of each entry is the one the editor's Code block dialog inserts, and the
//! Markdown guide lists by: Weesite's ID for the language from before the switch to syntect, so
//! posts and habits keep working. Every fence name Weesite took before maps to a syntax here,
//! where bat's set has one. A syntax this table doesn't name is listed by its own file extensions
//! and, when it's one word, its name.

/// A language as Weesite lists it.
pub struct Language {
    /// The syntax's name in the set (`SyntaxReference::name`).
    pub syntax: &'static str,
    /// The name the editor shows.
    pub name: &'static str,
    /// Its fence names, the one the editor inserts first.
    pub fences: &'static [&'static str],
}

/// The languages Weesite names itself, sorted by syntax. Those bat's set lacks are Weesite's own
/// (grammars/), converted from the TextMate grammars it highlighted them with before.
// A table: one language a line, which rustfmt would spread over several.
#[rustfmt::skip]
pub const LANGUAGES: &[Language] = &[
    lang("AsciiDoc (Asciidoctor)", "Asciidoc", &["asciidoc", "asciidoctor", "adoc"]),
    lang("Batch File", "Batch", &["bat", "batch", "cmd"]),
    lang("BibTeX", "BibTeX", &["bibtex", "bib"]),
    lang("Bicep", "Bicep", &["bicep", "bicepparam"]),
    lang("Bourne Again Shell (bash)", "Shell Script", &["shellscript", "bash", "sh", "zsh", "ksh", "csh", "console", "shell"]),
    lang("C", "C", &["c", "h"]),
    lang("C#", "C#", &["csharp", "c#", "cs"]),
    // CUDA C++ had a grammar of its own before; C++'s is close.
    lang("C++", "C++", &["cpp", "c++", "cuda-cpp", "cc", "cxx", "hpp"]),
    lang("CSS", "CSS", &["css"]),
    lang("Clojure", "Clojure", &["clojure", "clj"]),
    lang("CoffeeScript", "CoffeeScript", &["coffeescript", "coffee"]),
    lang("Dart", "Dart", &["dart"]),
    lang("Diff", "Diff", &["diff", "patch"]),
    lang("Dockerfile", "Docker", &["dockerfile", "docker", "containerfile"]),
    lang("F#", "F#", &["fsharp", "f#", "fs"]),
    lang("Git Ignore", "Ignore", &["ignore", "gitignore"]),
    lang("Go", "Go", &["go"]),
    lang("Groovy", "Groovy", &["groovy"]),
    lang("HLSL", "HLSL", &["hlsl"]),
    lang("HTML", "HTML", &["html", "htm", "xhtml"]),
    lang("Handlebars", "Handlebars", &["handlebars", "hbs"]),
    lang("Haskell", "Haskell", &["haskell", "hs"]),
    lang("INI", "Ini", &["ini"]),
    // JSON with Comments had a grammar of its own before; JSON's reads it.
    lang("JSON", "JSON", &["json", "jsonc"]),
    lang("Java", "Java", &["java"]),
    lang("Java Properties", "Properties", &["properties"]),
    // bat's own choice for .js: it reads modern JavaScript and JSX, so JavaScript React goes here too.
    lang("JavaScript (Babel)", "JavaScript", &["javascript", "js", "javascriptreact", "jsx", "mjs", "cjs"]),
    lang("Julia", "Julia", &["julia", "jl"]),
    lang("Just", "Just", &["just", "justfile"]),
    lang("LaTeX", "LaTeX", &["latex"]),
    lang("Less", "Less", &["less"]),
    lang("Lua", "Lua", &["lua"]),
    lang("Makefile", "Makefile", &["makefile", "make", "mk"]),
    lang("Markdown", "Markdown", &["markdown", "md"]),
    lang("Objective-C", "Objective-C", &["objective-c", "objc"]),
    lang("Objective-C++", "Objective-C++", &["objective-cpp", "objective-c++"]),
    lang("PHP", "PHP", &["php"]),
    lang("Pascal", "Pascal", &["pascal"]),
    lang("Perl", "Perl", &["perl", "pl"]),
    lang("Perl 6", "Perl 6", &["perl6", "raku", "p6"]),
    lang("PowerShell", "PowerShell", &["powershell", "ps", "ps1", "pwsh"]),
    lang("Protocol Buffer", "Protocol Buffers", &["proto", "protobuf", "proto3"]),
    lang("Pug", "Pug", &["jade", "pug"]),
    lang("Python", "Python", &["python", "py"]),
    lang("R", "R", &["r"]),
    lang("Razor", "Razor", &["razor", "cshtml"]),
    // Weesite's own (grammars/rego), from the OPA project: bat's Rego predates Rego v1 (`if`).
    lang("Rego (OPA)", "Rego", &["rego"]),
    lang("Ruby", "Ruby", &["ruby", "rb"]),
    lang("Rust", "Rust", &["rust", "rs"]),
    lang("SCSS", "SCSS", &["scss"]),
    lang("SQL", "SQL", &["sql"]),
    lang("ShaderLab", "ShaderLab", &["shaderlab", "shader"]),
    lang("Swift", "Swift", &["swift"]),
    lang("TOML", "TOML", &["toml"]),
    lang("TeX", "TeX", &["tex"]),
    // Plain HCL had a grammar of its own before; Terraform's is HCL with Terraform's blocks.
    lang("Terraform", "Terraform", &["terraform", "tf", "tofu", "opentofu", "hcl"]),
    lang("TypeScript", "TypeScript", &["typescript", "ts"]),
    lang("TypeScriptReact", "TypeScript React", &["typescriptreact", "tsx"]),
    // Typst's code mode had a grammar of its own before.
    lang("Typst", "Typst", &["typst", "typ", "typst-code", "typc"]),
    lang("Visual Basic", "Visual Basic", &["vb", "vbnet"]),
    lang("XML", "XML", &["xml", "xsl", "xslt", "svg"]),
    // Compose files are YAML.
    lang("YAML", "YAML", &["yaml", "yml", "dockercompose", "compose"]),
    lang("log", "Log", &["log"]),
    lang("x86_64 Assembly", "x86 Assembly", &["x86asm", "x86", "assembly", "asm"]),
];

const fn lang(
    syntax: &'static str,
    name: &'static str,
    fences: &'static [&'static str],
) -> Language {
    Language {
        syntax,
        name,
        fences,
    }
}
