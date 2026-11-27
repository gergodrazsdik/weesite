//! Writes the licence notices of the syntaxes the highlighter embeds (bat's, through two-face)
//! as Markdown, with line endings made `\n` (some upstream texts use `\r\n`).
//! `SYNTAX-LICENSES.md` is its output; regenerate it with each two-face update:
//!
//!     cargo run --example syntax-licenses > SYNTAX-LICENSES.md

fn main() {
    print!(
        "{}",
        two_face::acknowledgement::listing()
            .to_md()
            .replace("\r\n", "\n")
    );
}
