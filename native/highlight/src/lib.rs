//! Weesite's code highlighter (ADR 0012): syntect with bat's Sublime syntaxes (two-face), behind a
//! C interface the .NET app calls through P/Invoke.
//!
//! A block of code in a language becomes HTML: each token's text, escaped, in a
//! `<span class="hl-…">` for the first of Weesite's classes its scopes match, as Weesite's
//! highlighter did with TextMate scopes. The syntax set loads once per process, on the first call,
//! and syntect compiles each pattern on first use and keeps it, so later blocks are warm. The set is
//! safe to share between threads: each call keeps its own parse state.
//!
//! Limits, as before: a line longer than the limit, and every line after it, stays plain; so does
//! the rest of a block once the time it was given is spent, checked between lines. Within a line,
//! Oniguruma's retry limits bound how long one pattern may backtrack.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::LazyLock;
use std::time::{Duration, Instant};

use syntect::parsing::{ParseState, Scope, ScopeStack, ScopeStackOp, SyntaxReference, SyntaxSet};

/// Weesite's classes by scope prefix, most specific first. The innermost scope with a matching
/// prefix gives a token its class; the first matching prefix wins.
const SCOPE_CLASSES: &[(&str, &str)] = &[
    ("comment", "hl-comment"),
    ("string", "hl-string"),
    ("constant.character.escape", "hl-string"),
    ("constant.numeric", "hl-number"),
    ("constant.language", "hl-constant"),
    ("constant.character", "hl-constant"),
    ("constant.other", "hl-constant"),
    // Enum members, and HCL's block labels (resource "type" "name"): coloured as constants, as
    // editors' themes do (F-0164).
    ("variable.other.enummember", "hl-constant"),
    ("keyword.operator", "hl-operator"),
    ("keyword", "hl-keyword"),
    ("storage", "hl-keyword"),
    ("entity.name.function", "hl-function"),
    ("support.function", "hl-function"),
    ("entity.name.type", "hl-type"),
    ("entity.name.class", "hl-type"),
    ("entity.other.inherited-class", "hl-type"),
    ("support.type.property-name", "hl-property"),
    ("support.type", "hl-type"),
    ("support.class", "hl-type"),
    ("entity.name.tag", "hl-tag"),
    ("entity.other.attribute-name", "hl-attribute"),
    ("variable.other.property", "hl-property"),
    ("meta.object-literal.key", "hl-property"),
];

/// Fence names the syntaxes don't answer to, mapped to a name or extension they do.
const ALIASES: &[(&str, &str)] = &[
    ("c#", "cs"),
    ("csharp", "cs"),
    ("console", "bash"),
    ("shell", "bash"),
    ("shellscript", "bash"),
    ("pwsh", "ps1"),
    ("yml", "yaml"),
];

/// How many times Oniguruma may backtrack in one match attempt (its default is 10 million).
const RETRY_LIMIT_IN_MATCH: std::os::raw::c_ulong = 1_000_000;

/// How many times it may backtrack in one search, over every starting position (unlimited by default).
const RETRY_LIMIT_IN_SEARCH: std::os::raw::c_ulong = 10_000_000;

static SYNTAXES: LazyLock<SyntaxSet> = LazyLock::new(|| {
    // SAFETY: these set Oniguruma's process-wide defaults, which every search reads when it
    // starts; they're set here, before the first pattern in this library compiles or runs.
    unsafe {
        onig_sys::onig_set_retry_limit_in_match(RETRY_LIMIT_IN_MATCH);
        onig_sys::onig_set_retry_limit_in_search(RETRY_LIMIT_IN_SEARCH);
    }
    // Lines are passed with their newline, which bat's syntaxes are written for.
    two_face::syntax::extra_newlines()
});

/// Finds the syntax for a fence name: Weesite's aliases first, then syntect's own lookup (a file
/// extension, then the syntax's name), ignoring case.
pub fn find(language: &str) -> Option<&'static SyntaxReference> {
    let set: &'static SyntaxSet = &SYNTAXES;
    let lower = language.trim().to_ascii_lowercase();
    if lower.is_empty() {
        return None;
    }

    let token = ALIASES
        .iter()
        .find(|(alias, _)| *alias == lower)
        .map_or(lower.as_str(), |(_, target)| target);
    set.find_syntax_by_token(token)
        .filter(|syntax| syntax.name != "Plain Text")
}

/// Why a block stopped being coloured before its end.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Stop {
    /// A line was longer than the limit.
    TooLong,
    /// The time the block was given was spent.
    OutOfTime,
    /// The syntax failed on a line.
    Failed,
}

/// A highlighted block.
#[derive(Debug)]
pub struct Highlighted {
    /// The HTML, for inside `<code>`.
    pub html: String,
    /// How long it took.
    pub spent: Duration,
    /// Why colouring stopped before the end, if it did.
    pub stop: Option<Stop>,
}

/// Highlights a block: lines split at `\n` (`\r\n` counts as one), each coloured until one is longer
/// than `max_line_chars` or `budget` is spent; the rest stays plain, and every line is kept.
pub fn highlight(
    syntax: &SyntaxReference,
    code: &str,
    budget: Duration,
    max_line_chars: usize,
) -> Highlighted {
    let started = Instant::now();
    let set: &SyntaxSet = &SYNTAXES;
    let code = code.replace("\r\n", "\n");
    let mut html = String::with_capacity(code.len() * 2);
    let mut state = ParseState::new(syntax);
    let mut stack = ScopeStack::new();
    let mut classes = HashMap::new();
    let mut stop = None;
    let mut line_with_newline = String::new();

    for (i, line) in code.split('\n').enumerate() {
        if i > 0 {
            html.push('\n');
        }

        if stop.is_none() {
            if line.chars().count() > max_line_chars {
                stop = Some(Stop::TooLong);
            } else if started.elapsed() >= budget {
                stop = Some(Stop::OutOfTime);
            }
        }

        if stop.is_some() {
            escape_into(&mut html, line);
            continue;
        }

        line_with_newline.clear();
        line_with_newline.push_str(line);
        line_with_newline.push('\n');
        let written = html.len();
        let coloured = state
            .parse_line(&line_with_newline, set)
            .ok()
            .and_then(|ops| write_line(&mut html, line, &ops, &mut stack, &mut classes));
        if coloured.is_none() {
            // A line the syntax fails on stays plain, and so does the rest: the state is lost.
            html.truncate(written);
            escape_into(&mut html, line);
            stop = Some(Stop::Failed);
        }
    }

    Highlighted {
        html,
        spent: started.elapsed(),
        stop,
    }
}

// Writes one line from its scope changes, merging neighbouring tokens of the same class into one
// span. None when the scopes don't add up, which the caller treats as a failed line.
fn write_line(
    html: &mut String,
    line: &str,
    ops: &[(usize, ScopeStackOp)],
    stack: &mut ScopeStack,
    classes: &mut HashMap<Scope, Option<&'static str>>,
) -> Option<()> {
    let mut open: Option<&'static str> = None;
    let mut position = 0;
    for (offset, op) in ops {
        let end = (*offset).min(line.len());
        if end > position {
            let class = class_of(stack, classes);
            write_token(html, &mut open, class, &line[position..end]);
            position = end;
        }

        stack.apply(op).ok()?;
    }

    if position < line.len() {
        let class = class_of(stack, classes);
        write_token(html, &mut open, class, &line[position..]);
    }

    if open.is_some() {
        html.push_str("</span>");
    }

    Some(())
}

fn write_token(
    html: &mut String,
    open: &mut Option<&'static str>,
    class: Option<&'static str>,
    text: &str,
) {
    if *open != class {
        if open.is_some() {
            html.push_str("</span>");
        }

        if let Some(class) = class {
            let _ = write!(html, "<span class=\"{class}\">");
        }

        *open = class;
    }

    escape_into(html, text);
}

fn class_of(
    stack: &ScopeStack,
    classes: &mut HashMap<Scope, Option<&'static str>>,
) -> Option<&'static str> {
    stack.as_slice().iter().rev().find_map(|scope| {
        *classes.entry(*scope).or_insert_with(|| {
            let name = scope.build_string();
            SCOPE_CLASSES
                .iter()
                .find(|(prefix, _)| name.starts_with(prefix))
                .map(|(_, class)| *class)
        })
    })
}

fn escape_into(html: &mut String, text: &str) {
    for c in text.chars() {
        match c {
            '&' => html.push_str("&amp;"),
            '<' => html.push_str("&lt;"),
            '>' => html.push_str("&gt;"),
            '"' => html.push_str("&quot;"),
            '\'' => html.push_str("&#39;"),
            _ => html.push(c),
        }
    }
}

/// The languages, as JSON: `[{"name":"C#","fences":["c#","cs","csharp","csx"]}, …]`, sorted by
/// name; each syntax's fence names are its file extensions, its name when it's one word, and the
/// aliases that lead to it. Hidden syntaxes and plain text are left out.
pub fn languages_json() -> String {
    let set: &SyntaxSet = &SYNTAXES;
    let mut languages: Vec<(&str, Vec<String>)> = set
        .syntaxes()
        .iter()
        .filter(|syntax| !syntax.hidden && syntax.name != "Plain Text")
        .map(|syntax| {
            let mut fences: Vec<String> = syntax
                .file_extensions
                .iter()
                .map(|extension| extension.to_ascii_lowercase())
                .filter(|extension| !extension.contains(char::is_whitespace))
                .collect();
            if !syntax.name.contains(char::is_whitespace) {
                fences.push(syntax.name.to_ascii_lowercase());
            }

            for (alias, _) in ALIASES {
                if find(alias).is_some_and(|found| found.name == syntax.name) {
                    fences.push((*alias).to_owned());
                }
            }

            fences.sort();
            fences.dedup();
            (syntax.name.as_str(), fences)
        })
        .collect();
    languages.sort_by_key(|(name, _)| name.to_ascii_lowercase());

    let mut json = String::from("[");
    for (i, (name, fences)) in languages.iter().enumerate() {
        if i > 0 {
            json.push(',');
        }

        json.push_str("{\"name\":");
        json_string_into(&mut json, name);
        json.push_str(",\"fences\":[");
        for (j, fence) in fences.iter().enumerate() {
            if j > 0 {
                json.push(',');
            }

            json_string_into(&mut json, fence);
        }

        json.push_str("]}");
    }

    json.push(']');
    json
}

fn json_string_into(json: &mut String, text: &str) {
    json.push('"');
    for c in text.chars() {
        match c {
            '"' => json.push_str("\\\""),
            '\\' => json.push_str("\\\\"),
            c if (c as u32) < 0x20 => {
                let _ = write!(json, "\\u{:04x}", c as u32);
            }
            c => json.push(c),
        }
    }

    json.push('"');
}

/// The result of [`weesite_highlight`], for the caller to read and then free with
/// [`weesite_highlight_free`].
#[repr(C)]
pub struct HighlightResult {
    /// The HTML, UTF-8, not terminated.
    pub html: *mut u8,
    /// Its length in bytes.
    pub html_len: usize,
    /// How long highlighting took, in microseconds.
    pub spent_micros: u64,
    /// Why colouring stopped before the end: 0 it didn't, 1 a line too long, 2 out of time, 3 the
    /// syntax failed.
    pub stop: u32,
}

/// The result codes of the C interface.
const OK: i32 = 0;
const UNKNOWN_LANGUAGE: i32 = 1;
const BAD_ARGUMENTS: i32 = -1;
const FAILED: i32 = -2;

/// Highlights a block of code in a language (a fence name).
///
/// Returns 0 with `result` filled in, 1 when no syntax answers to the language (nothing to free),
/// -1 for a null pointer or text that isn't UTF-8, -2 when highlighting panicked. A panic never
/// crosses this boundary.
///
/// # Safety
///
/// `language` and `code` must point to `language_len` and `code_len` readable bytes (either may be
/// null when its length is 0), and `result` to a writable [`HighlightResult`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn weesite_highlight(
    language: *const u8,
    language_len: usize,
    code: *const u8,
    code_len: usize,
    budget_micros: u64,
    max_line_chars: u32,
    result: *mut HighlightResult,
) -> i32 {
    if result.is_null() {
        return BAD_ARGUMENTS;
    }

    // SAFETY: the caller passes readable buffers of these lengths, or null with a length of 0.
    let (Some(language), Some(code)) = (unsafe { text(language, language_len) }, unsafe {
        text(code, code_len)
    }) else {
        return BAD_ARGUMENTS;
    };

    let outcome = catch_unwind(AssertUnwindSafe(|| {
        find(language).map(|syntax| {
            highlight(
                syntax,
                code,
                Duration::from_micros(budget_micros),
                max_line_chars as usize,
            )
        })
    }));
    match outcome {
        Ok(Some(highlighted)) => {
            let (html, html_len) = into_raw(highlighted.html);
            let written = HighlightResult {
                html,
                html_len,
                spent_micros: u64::try_from(highlighted.spent.as_micros()).unwrap_or(u64::MAX),
                stop: match highlighted.stop {
                    None => 0,
                    Some(Stop::TooLong) => 1,
                    Some(Stop::OutOfTime) => 2,
                    Some(Stop::Failed) => 3,
                },
            };
            // SAFETY: the caller passes a writable HighlightResult.
            unsafe { result.write(written) };
            OK
        }
        Ok(None) => UNKNOWN_LANGUAGE,
        Err(_) => FAILED,
    }
}

/// Gives the languages as UTF-8 JSON (see [`languages_json`]), to free with
/// [`weesite_highlight_free`]. Returns 0, -1 for a null pointer, or -2 when it panicked.
///
/// # Safety
///
/// `json` and `json_len` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn weesite_highlight_languages(
    json: *mut *mut u8,
    json_len: *mut usize,
) -> i32 {
    if json.is_null() || json_len.is_null() {
        return BAD_ARGUMENTS;
    }

    match catch_unwind(languages_json) {
        Ok(text) => {
            let (pointer, length) = into_raw(text);
            // SAFETY: the caller passes writable pointers.
            unsafe {
                json.write(pointer);
                json_len.write(length);
            }
            OK
        }
        Err(_) => FAILED,
    }
}

/// Frees text this library handed out.
///
/// # Safety
///
/// `text` and `len` must come from one result of this library, and be freed once; null is ignored.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn weesite_highlight_free(text: *mut u8, len: usize) {
    if !text.is_null() {
        // SAFETY: the pointer and length came from into_raw, so they make up a boxed slice.
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(text, len)) });
    }
}

// SAFETY (for callers): `pointer` is null with a length of 0, or points to `len` readable bytes.
unsafe fn text<'a>(pointer: *const u8, len: usize) -> Option<&'a str> {
    if len == 0 {
        return Some("");
    }

    if pointer.is_null() {
        return None;
    }

    // SAFETY: the caller's promise above.
    std::str::from_utf8(unsafe { std::slice::from_raw_parts(pointer, len) }).ok()
}

fn into_raw(text: String) -> (*mut u8, usize) {
    let bytes = text.into_bytes().into_boxed_slice();
    let len = bytes.len();
    (Box::into_raw(bytes).cast::<u8>(), len)
}

#[cfg(test)]
mod tests {
    use super::*;

    const ENOUGH: Duration = Duration::from_secs(5);

    fn colour(language: &str, code: &str) -> Highlighted {
        highlight(find(language).expect("a syntax"), code, ENOUGH, 1_000)
    }

    #[test]
    fn keywords_strings_and_comments_get_weesite_classes() {
        let out = colour("csharp", "// a note\nvar name = \"Ada\";");

        assert!(
            out.html
                .contains("<span class=\"hl-comment\">// a note</span>"),
            "{}",
            out.html
        );
        assert!(
            out.html.contains("<span class=\"hl-keyword\">var</span>"),
            "{}",
            out.html
        );
        assert!(out.html.contains("hl-string"), "{}", out.html);
        assert_eq!(out.stop, None);
    }

    #[test]
    fn the_more_specific_prefix_wins() {
        // keyword.operator comes before keyword in the table.
        let out = colour("js", "a = b + c;");

        assert!(
            out.html.contains("<span class=\"hl-operator\">"),
            "{}",
            out.html
        );
    }

    #[test]
    fn fence_names_resolve_through_the_aliases_and_ignore_case() {
        for name in ["cs", "c#", "csharp", "CSharp"] {
            assert_eq!(find(name).map(|s| s.name.as_str()), Some("C#"), "{name}");
        }

        assert!(find("yml").is_some());
        assert!(find("pwsh").is_some());
        assert!(find("console").is_some());
        assert!(find("no-such-language").is_none());
        assert!(find("").is_none());
        assert!(find("txt").is_none(), "plain text is no syntax");
    }

    #[test]
    fn text_is_escaped_and_every_line_kept() {
        let out = colour("html", "<a href=\"x\">&'</a>\r\n\nend");

        assert!(!out.html.contains("<a "), "{}", out.html);
        assert!(out.html.contains("&lt;"), "{}", out.html);
        assert!(out.html.contains("&amp;"), "{}", out.html);
        assert!(out.html.contains("&#39;"), "{}", out.html);
        assert_eq!(out.html.matches('\n').count(), 2, "{}", out.html);
        assert!(!out.html.contains('\r'));
    }

    #[test]
    fn a_line_over_the_limit_and_the_rest_stay_plain() {
        let long = "x".repeat(20);
        let code = format!("var a = 1;\n{long}\nvar b = 2;");

        let out = highlight(find("cs").unwrap(), &code, ENOUGH, 10);

        assert_eq!(out.stop, Some(Stop::TooLong));
        assert!(out.html.starts_with("<span"), "{}", out.html);
        assert!(
            out.html.ends_with(&format!("{long}\nvar b = 2;")),
            "{}",
            out.html
        );
    }

    #[test]
    fn without_time_everything_stays_plain() {
        let out = highlight(find("cs").unwrap(), "var a = 1;", Duration::ZERO, 1_000);

        assert_eq!(out.stop, Some(Stop::OutOfTime));
        assert_eq!(out.html, "var a = 1;");
    }

    #[test]
    fn the_languages_are_json_with_their_fence_names() {
        let json = languages_json();

        assert!(json.starts_with("[{\"name\":"), "{json}");
        assert!(
            json.contains("{\"name\":\"C#\",\"fences\":[\"c#\",\"cs\",\"csharp\",\"csx\"]}"),
            "{json}"
        );
        assert!(!json.contains("\"Plain Text\""));
    }

    #[test]
    fn the_c_interface_highlights_frees_and_refuses_what_it_should() {
        let language = "cs";
        let code = "var a = 1;";
        let mut result = HighlightResult {
            html: std::ptr::null_mut(),
            html_len: 0,
            spent_micros: 0,
            stop: 9,
        };

        // SAFETY: valid buffers and a writable result.
        let status = unsafe {
            weesite_highlight(
                language.as_ptr(),
                language.len(),
                code.as_ptr(),
                code.len(),
                5_000_000,
                1_000,
                &mut result,
            )
        };
        assert_eq!(status, OK);
        assert_eq!(result.stop, 0);
        // SAFETY: the library just handed out this buffer.
        let html = unsafe {
            std::str::from_utf8(std::slice::from_raw_parts(result.html, result.html_len))
                .unwrap()
                .to_owned()
        };
        assert!(html.contains("hl-keyword"), "{html}");
        // SAFETY: freed once, as handed out.
        unsafe { weesite_highlight_free(result.html, result.html_len) };

        let unknown = "nope";
        // SAFETY: as above.
        let status = unsafe {
            weesite_highlight(
                unknown.as_ptr(),
                unknown.len(),
                code.as_ptr(),
                code.len(),
                5_000_000,
                1_000,
                &mut result,
            )
        };
        assert_eq!(status, UNKNOWN_LANGUAGE);

        let invalid = [0xff_u8, 0xfe];
        // SAFETY: as above; the bytes aren't UTF-8 on purpose.
        let status = unsafe {
            weesite_highlight(
                language.as_ptr(),
                language.len(),
                invalid.as_ptr(),
                invalid.len(),
                5_000_000,
                1_000,
                &mut result,
            )
        };
        assert_eq!(status, BAD_ARGUMENTS);

        // SAFETY: a null result is refused before anything is read.
        let status = unsafe {
            weesite_highlight(
                language.as_ptr(),
                language.len(),
                code.as_ptr(),
                code.len(),
                1,
                1,
                std::ptr::null_mut(),
            )
        };
        assert_eq!(status, BAD_ARGUMENTS);
    }
}
