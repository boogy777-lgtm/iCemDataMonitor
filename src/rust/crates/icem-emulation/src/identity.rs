//! Catalog/control name equality, separate from ASCII protocol tokens and JSON keys.
//!
//! The checked table is extracted from the actual BCL OrdinalIgnoreCase comparer,
//! not Rust casing or Unicode full case folding. See tools/ordinal-identity-baseline.json.
//! .NET's ordinal algorithm compares individual BMP characters or valid surrogate
//! pairs without expansions/context. Thus on Rust scalar strings its equality is
//! the pointwise scalar equivalence relation; UTF-16 width is preserved by every
//! checked class. We need equality only, not the .NET lexical ordering.
//! Source: https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Globalization/OrdinalCasing.Icu.cs.html
//! Provider/version changes require the exhaustive BCL -Check and Windows evidence.

#[path = "identity_table.rs"]
mod table;

fn representative(value: char) -> char {
    if value.is_ascii() {
        return value.to_ascii_uppercase();
    }
    match table::MAPPINGS.binary_search_by_key(&value, |&(source, _)| source) {
        Ok(index) => table::MAPPINGS[index].1,
        Err(_) => value,
    }
}

/// Equality for valid Unicode scalar strings under the recorded BCL baseline.
pub(crate) fn eq_ignore_case(left: &str, right: &str) -> bool {
    if left.is_ascii() && right.is_ascii() {
        return left.eq_ignore_ascii_case(right);
    }
    left.chars()
        .map(representative)
        .eq(right.chars().map(representative))
}

/// A checked name identity: raw display strings cannot enter identity-keyed maps.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct Key(String);

/// Stable identity key. Keep original spelling separately for external output.
pub(crate) fn key(value: &str) -> Key {
    if value.is_ascii() {
        return Key(value.to_ascii_uppercase());
    }
    Key(value.chars().map(representative).collect())
}

#[cfg(test)]
mod tests {
    use super::{eq_ignore_case, key, representative, table};

    #[test]
    fn complete_scalar_domain_has_sorted_minimal_idempotent_classes() {
        assert_eq!(table::MAPPINGS.len(), 1470);
        assert!(table::MAPPINGS.windows(2).all(|pair| pair[0].0 < pair[1].0));
        // Fixed ASCII protocol/DFB names may retain ASCII-only comparison:
        // this baseline has no ordinal class crossing the ASCII boundary.
        assert!(
            table::MAPPINGS
                .iter()
                .all(|&(source, target)| source.is_ascii() == target.is_ascii())
        );
        for &(source, target) in table::MAPPINGS {
            assert!(target < source);
            assert_eq!(representative(source), target);
            assert_eq!(source.len_utf16(), target.len_utf16());
            assert_eq!(representative(target), target);
        }
        let mut scalar_count = 0;
        for value in (0..=0x10ffff).filter_map(char::from_u32) {
            scalar_count += 1;
            let mapped = representative(value);
            assert_eq!(representative(mapped), mapped);
            assert_eq!(value.len_utf16(), mapped.len_utf16());
        }
        assert_eq!(scalar_count, 1_112_064);
    }

    #[test]
    fn ordinal_identity_preserves_nonfolding_and_scalar_boundaries() {
        for (left, right, equal) in [
            ("Давление", "ДАВЛЕНИЕ", true),
            ("σ", "ς", true),
            ("\u{10400}", "\u{10428}", true),
            ("i", "I", true),
            ("ı", "I", false),
            ("İ", "i", false),
            ("ſ", "S", false),
            ("ß", "SS", false),
            ("ß", "ẞ", false),
            ("é", "e\u{301}", false),
            ("K", "K", false),
            ("", "", true),
            ("\0", "", false),
        ] {
            assert_eq!(eq_ignore_case(left, right), equal, "{left:?}/{right:?}");
            assert_eq!(key(left) == key(right), equal, "{left:?}/{right:?}");
            let wrapped_left = format!("\0A\u{10400}{left}\u{301}z");
            let wrapped_right = format!("\0a\u{10428}{right}\u{301}Z");
            assert_eq!(eq_ignore_case(&wrapped_left, &wrapped_right), equal);
            assert_eq!(key(&wrapped_left) == key(&wrapped_right), equal);
        }
    }

    #[cfg(windows)]
    #[test]
    fn all_scalars_match_independent_windows_bcl() -> Result<(), Box<dyn std::error::Error>> {
        let script = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../tests/support/OrdinalIdentityOracle.ps1");
        let output = std::process::Command::new("pwsh")
            .args(["-NoProfile", "-File"])
            .arg(script)
            .output()?;
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let oracle: serde_json::Value = serde_json::from_slice(&output.stdout)?;
        eprintln!(
            "BCL identity provenance: runtime={}, os={}, useNls={}, invariant={}",
            oracle["runtime"], oracle["os"], oracle["useNls"], oracle["invariant"]
        );
        assert_eq!(oracle["scalarCount"], 1_112_064);
        let pairs: Vec<[u32; 2]> = serde_json::from_value(oracle["map"].clone())?;
        let expected: std::collections::BTreeMap<u32, u32> = pairs
            .iter()
            .map(|&[source, target]| (source, target))
            .collect();
        assert_eq!(expected.len(), pairs.len(), "Duplicate oracle source");
        for value in (0..=0x10ffff).filter_map(char::from_u32) {
            let target = expected
                .get(&u32::from(value))
                .copied()
                .unwrap_or(u32::from(value));
            let target = char::from_u32(target).ok_or("Non-scalar oracle representative")?;
            assert_eq!(
                key(&value.to_string()).0,
                target.to_string(),
                "BCL provider drift at U+{:X}",
                u32::from(value)
            );
            assert!(eq_ignore_case(&value.to_string(), &target.to_string()));
        }
        Ok(())
    }
}
