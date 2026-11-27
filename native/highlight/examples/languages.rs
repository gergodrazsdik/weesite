//! Writes the table of languages in docs/grammars.md, between its `languages:begin` and
//! `languages:end` markers: each language's name and fence names, as the editor lists them.
//!
//!     cargo run --example languages -- --write ../../docs/grammars.md
//!     cargo run --example languages -- --check ../../docs/grammars.md

const BEGIN: &str = "<!-- languages:begin -->";
const END: &str = "<!-- languages:end -->";

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let (Some(mode), Some(path)) = (args.first(), args.get(1)) else {
        eprintln!("usage: languages --write|--check <grammars.md>");
        std::process::exit(2);
    };

    let doc = std::fs::read_to_string(path).expect("the document reads");
    let (Some(start), Some(end)) = (doc.find(BEGIN), doc.find(END)) else {
        eprintln!("{path} has no {BEGIN} … {END} markers");
        std::process::exit(2);
    };

    let updated = format!("{}{BEGIN}\n\n{}\n{}", &doc[..start], table(), &doc[end..]);
    match mode.as_str() {
        "--write" => std::fs::write(path, updated).expect("the document writes"),
        "--check" if updated != doc => {
            eprintln!(
                "{path}: the list of languages is out of date; run `cargo run --example languages -- --write {path}` in native/highlight"
            );
            std::process::exit(1);
        }
        "--check" => {}
        _ => {
            eprintln!("usage: languages --write|--check <grammars.md>");
            std::process::exit(2);
        }
    }
}

fn table() -> String {
    let json = weesite_highlight::languages_json();
    let mut rows = vec![
        "| Language | Fence names |".to_owned(),
        "| --- | --- |".to_owned(),
    ];
    // The JSON is the library's own, flat and escaped: read it without a JSON crate.
    for entry in json.trim_matches(['[', ']']).split("},{") {
        let entry = entry.trim_matches(['{', '}']);
        let name = between(entry, "\"name\":\"", "\",\"fences\"");
        let fences: Vec<String> = between(entry, "\"fences\":[", "]")
            .split(',')
            .map(|fence| format!("`{}`", fence.trim_matches('"').replace("\\\"", "\"")))
            .collect();
        rows.push(format!(
            "| {} | {} |",
            name.replace("\\\"", "\"").replace('|', "\\|"),
            fences.join(", ").replace('|', "\\|")
        ));
    }

    rows.join("\n") + "\n"
}

fn between<'a>(text: &'a str, open: &str, close: &str) -> &'a str {
    let start = text.find(open).expect("the field is there") + open.len();
    let end = text[start..].find(close).expect("the field ends") + start;
    &text[start..end]
}
