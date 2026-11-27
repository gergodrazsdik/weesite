//! Prints each token of a block with its scopes and the class Weesite gives it, to see how a
//! syntax reads some code when choosing or checking class mappings:
//!
//!     cargo run --example scopes -- terraform 'resource "a" "b" {}'

use syntect::parsing::{ParseState, ScopeStack, SyntaxSet};

fn main() {
    let mut args = std::env::args().skip(1);
    let (Some(language), Some(code)) = (args.next(), args.next()) else {
        eprintln!("usage: scopes <fence name> <code>");
        std::process::exit(2);
    };

    let syntax = weesite_highlight::find(&language).unwrap_or_else(|| {
        eprintln!("no syntax for {language}");
        std::process::exit(1);
    });
    println!("{language}: {}", syntax.name);
    let set: SyntaxSet = two_face::syntax::extra_newlines();
    let syntax = set.find_syntax_by_name(&syntax.name).expect("the same set");
    let mut state = ParseState::new(syntax);
    let mut stack = ScopeStack::new();
    for line in code.split('\n') {
        let with_newline = format!("{line}\n");
        let ops = state.parse_line(&with_newline, &set).expect("parses");
        let mut position = 0;
        for (offset, op) in ops.iter().chain(std::iter::once(&(
            with_newline.len(),
            syntect::parsing::ScopeStackOp::Noop,
        ))) {
            let end = (*offset).min(line.len());
            if end > position {
                let scopes: Vec<String> =
                    stack.as_slice().iter().map(|s| s.build_string()).collect();
                println!(
                    "{:>20} | {}",
                    format!("{:?}", &line[position..end]),
                    scopes.join(" ")
                );
                position = end;
            }

            stack.apply(op).expect("scopes add up");
        }
    }
}
