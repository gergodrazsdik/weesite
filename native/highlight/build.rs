//! Builds the highlighter's syntax set (T-0189): bat's syntaxes (two-face) and Weesite's own from
//! grammars/ (converted from TextMate by scripts/convert-grammars.py), written as one dump the
//! library embeds and loads on its first call.

use std::env;
use std::path::PathBuf;

fn main() {
    println!("cargo::rerun-if-changed=grammars");
    println!("cargo::rerun-if-changed=build.rs");

    // Added to bat's set, not in place of any of its syntaxes: those taken back out of a built set
    // refer to each other by position, so leaving one out breaks the rest. Where Weesite's
    // replaces bat's (Rego), it has a name of its own, and Weesite's fence names pick it.
    let mut builder = two_face::syntax::extra_newlines().into_builder();
    // Lines come with their newline, as for bat's syntaxes.
    builder
        .add_from_folder("grammars", true)
        .expect("the grammars in grammars/ load");
    let set = builder.build();

    let out = PathBuf::from(env::var_os("OUT_DIR").expect("cargo sets OUT_DIR"));
    syntect::dumps::dump_to_file(&set, out.join("syntaxes.packdump"))
        .expect("the syntax set is written");
}
