//! Catalog/control name equality, separate from ASCII protocol tokens and JSON keys.
//!
//! The checked table is extracted from the actual BCL OrdinalIgnoreCase comparer,
//! not Rust casing or Unicode full case folding. See tools/ordinal-identity-baseline.json.
//! .NET's ordinal algorithm compares individual BMP characters or valid surrogate
//! pairs without expansions/context. Thus on Rust scalar strings its equality is
//! the pointwise scalar equivalence relation; UTF-16 width is preserved by every
//! checked class. We need equality only, not the .NET lexical ordering.
//! Source: https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Globalization/OrdinalCasing.Icu.cs.html
//! The default build preserves the historical 19045 provider. The explicit
//! `ordinal-icu-26100` build feature selects the independently captured 26100
//! provider; selection is a deployment decision, not runtime OS detection.
//! Neither profile makes a claim about unmeasured BCL/ICU/NLS providers.

#[cfg(any(test, not(feature = "ordinal-icu-26100")))]
#[path = "identity_table.rs"]
mod table;
#[cfg(any(test, feature = "ordinal-icu-26100"))]
#[path = "identity_table_icu_26100.rs"]
mod table_icu_26100;

#[cfg(not(feature = "ordinal-icu-26100"))]
const ACTIVE: &[(char, char)] = table::MAPPINGS;
#[cfg(feature = "ordinal-icu-26100")]
const ACTIVE: &[(char, char)] = table_icu_26100::MAPPINGS;

fn representative_in(value: char, mappings: &[(char, char)]) -> char {
    if value.is_ascii() {
        return value.to_ascii_uppercase();
    }
    match mappings.binary_search_by_key(&value, |&(source, _)| source) {
        Ok(index) => mappings[index].1,
        Err(_) => value,
    }
}

fn representative(value: char) -> char {
    representative_in(value, ACTIVE)
}

/// Equality for valid Unicode scalar strings under the selected BCL profile.
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
    use super::{ACTIVE, eq_ignore_case, key, representative_in, table, table_icu_26100};

    #[test]
    fn complete_scalar_domain_has_sorted_minimal_idempotent_classes() {
        for (mappings, count) in [(table::MAPPINGS, 1462), (table_icu_26100::MAPPINGS, 1470)] {
            assert_eq!(mappings.len(), count);
            assert!(mappings.windows(2).all(|pair| pair[0].0 < pair[1].0));
            // Fixed ASCII protocol/DFB names may retain ASCII-only comparison:
            // neither supported profile has a class crossing the ASCII boundary.
            assert!(
                mappings
                    .iter()
                    .all(|&(source, target)| source.is_ascii() == target.is_ascii())
            );
            for &(source, target) in mappings {
                assert!(target < source);
                assert_eq!(representative_in(source, mappings), target);
                assert_eq!(source.len_utf16(), target.len_utf16());
                assert_eq!(representative_in(target, mappings), target);
            }
            let mut scalar_count = 0;
            for value in (0..=0x10ffff).filter_map(char::from_u32) {
                scalar_count += 1;
                let mapped = representative_in(value, mappings);
                assert_eq!(representative_in(mapped, mappings), mapped);
                assert_eq!(value.len_utf16(), mapped.len_utf16());
            }
            assert_eq!(scalar_count, 1_112_064);
        }
    }

    fn check_capture(
        mappings: &[(char, char)],
        captured: &str,
    ) -> Result<(), Box<dyn std::error::Error>> {
        let oracle: serde_json::Value = serde_json::from_str(captured)?;
        assert_eq!(oracle["scalarCount"], 1_112_064);
        assert_eq!(oracle["provider"], "ICU");
        assert_eq!(oracle["invariantActual"], false);
        assert_eq!(oracle["useNlsActual"], false);
        let pairs: Vec<[u32; 2]> = serde_json::from_value(oracle["map"].clone())?;
        let expected: std::collections::BTreeMap<u32, u32> =
            pairs.iter().map(|&[a, b]| (a, b)).collect();
        assert_eq!(expected.len(), pairs.len(), "Duplicate oracle source");
        assert_eq!(expected.len(), mappings.len(), "Unexpected mapping count");
        for value in (0..=0x10ffff).filter_map(char::from_u32) {
            let target = expected
                .get(&u32::from(value))
                .copied()
                .unwrap_or(u32::from(value));
            assert_eq!(
                u32::from(representative_in(value, mappings)),
                target,
                "Captured provider drift at U+{:X}",
                u32::from(value)
            );
        }
        Ok(())
    }

    #[test]
    fn both_full_captured_providers_match_their_tables() -> Result<(), Box<dyn std::error::Error>> {
        check_capture(
            table::MAPPINGS,
            include_str!("../../../tests/fixtures/ordinal-icu-19045.json"),
        )?;
        check_capture(
            table_icu_26100::MAPPINGS,
            include_str!("../../../tests/fixtures/ordinal-icu-26100.json"),
        )
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
            "BCL identity provenance: runtime={}, os={}, provider={}, useNls={}, invariant={}, selectedMappings={}",
            oracle["runtime"],
            oracle["os"],
            oracle["provider"],
            oracle["useNlsActual"],
            oracle["invariantActual"],
            ACTIVE.len()
        );
        assert_eq!(oracle["provider"], "ICU", "Unsupported BCL provider");
        assert_eq!(
            oracle["invariantActual"], false,
            "Unsupported invariant provider"
        );
        assert_eq!(oracle["useNlsActual"], false, "Unsupported NLS provider");
        #[cfg(not(feature = "ordinal-icu-26100"))]
        let (runtime, os, sort) = ("10.0.13", "Microsoft Windows 10.0.19045", 24985);
        #[cfg(feature = "ordinal-icu-26100")]
        let (runtime, os, sort) = ("10.0.14", "Microsoft Windows 10.0.26100", 31129);
        assert_eq!(oracle["runtime"], runtime, "Unsupported BCL runtime");
        assert_eq!(oracle["os"], os, "Unsupported Windows provider");
        assert_eq!(oracle["sortVersion"], sort, "Unsupported ICU data");
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
