//! BOM-aware text files corresponding to .NET `File.ReadAllText` consumers.
//!
//! This is not a decoder for JSON streams: those retain their UTF-8 boundary.
use std::{io, path::Path};

/// Read once, defaulting to UTF-8 and detecting an initial UTF-8/16/32 BOM.
/// I/O errors are returned; malformed text uses [`decode_read_all_text`]'s policy.
pub fn read_all_text(path: impl AsRef<Path>) -> io::Result<String> {
    Ok(decode_read_all_text(&std::fs::read(path)?))
}

/// Decode UTF-8 by default, or UTF-8/16LE/16BE/32LE/32BE with an initial BOM.
/// Exactly one BOM is consumed; subsequent U+FEFF characters remain content.
/// UTF-32 BOMs are detected before the overlapping UTF-16LE BOM.
///
/// The explicit replacement policy preserves the existing INI decoder: UTF-8
/// uses Rust's lossy decoding (one U+FFFD per invalid sequence); UTF-16 replaces
/// each unpaired surrogate and any final incomplete byte; UTF-32 replaces each
/// invalid scalar and any final incomplete group of one to three bytes. No
/// locale code page or encoding guess is used for files without a BOM.
pub fn decode_read_all_text(bytes: &[u8]) -> String {
    // File.ReadAllText BOM detection, including UTF-16/32 operator-edited files.
    let (body, width, little) = if bytes.starts_with(&[255, 254, 0, 0]) {
        (&bytes[4..], 4, true)
    } else if bytes.starts_with(&[0, 0, 254, 255]) {
        (&bytes[4..], 4, false)
    } else if bytes.starts_with(&[255, 254]) {
        (&bytes[2..], 2, true)
    } else if bytes.starts_with(&[254, 255]) {
        (&bytes[2..], 2, false)
    } else {
        return String::from_utf8_lossy(bytes.strip_prefix(&[239, 187, 191]).unwrap_or(bytes))
            .into_owned();
    };
    if width == 2 {
        let (pairs, tail) = body.as_chunks::<2>();
        let words: Vec<u16> = pairs
            .iter()
            .map(|pair| {
                if little {
                    u16::from_le_bytes(*pair)
                } else {
                    u16::from_be_bytes(*pair)
                }
            })
            .collect();
        let mut text = String::from_utf16_lossy(&words);
        if !tail.is_empty() {
            text.push(char::REPLACEMENT_CHARACTER);
        }
        text
    } else {
        let (groups, tail) = body.as_chunks::<4>();
        let mut text: String = groups
            .iter()
            .map(|group| {
                char::from_u32(if little {
                    u32::from_le_bytes(*group)
                } else {
                    u32::from_be_bytes(*group)
                })
                .unwrap_or(char::REPLACEMENT_CHARACTER)
            })
            .collect();
        if !tail.is_empty() {
            text.push(char::REPLACEMENT_CHARACTER);
        }
        text
    }
}

#[cfg(test)]
mod tests {
    use super::decode_read_all_text;

    #[test]
    fn malformed_sequences_have_explicit_replacement_boundaries() {
        let cases: &[(&[u8], &str)] = &[
            (&[b'A', 0xff, b'B'], "A\u{fffd}B"),
            (&[0xef, 0xbb, 0xbf, 0xe2, 0x82], "\u{fffd}"),
            (&[0xff, 0xfe, 0x00, 0xd8, b'A', 0], "\u{fffd}A"),
            (&[0xfe, 0xff, 0xdc, 0, 0, b'A'], "\u{fffd}A"),
            (&[0xff, 0xfe, b'A'], "\u{fffd}"),
            (&[0xfe, 0xff, 0xd8, 0, b'A'], "\u{fffd}\u{fffd}"),
            (&[0xff, 0xfe, 0, 0, 0, 0xd8, 0, 0], "\u{fffd}"),
            (&[0, 0, 0xfe, 0xff, 0, 0x11, 0, 0], "\u{fffd}"),
            (&[0xff, 0xfe, 0, 0, b'A'], "\u{fffd}"),
            (&[0, 0, 0xfe, 0xff, 0, 0, b'A'], "\u{fffd}"),
        ];
        for (bytes, expected) in cases {
            assert_eq!(decode_read_all_text(bytes), *expected, "{bytes:02x?}");
        }
    }

    #[test]
    fn only_one_bom_is_consumed_and_supplementary_scalars_survive() {
        let cases: &[(&[u8], &str)] = &[
            (&[], ""),
            (&[0xef, 0xbb, 0xbf], ""),
            (&[0xff, 0xfe], ""),
            (&[0xfe, 0xff], ""),
            (&[0xff, 0xfe, 0, 0], ""),
            (&[0, 0, 0xfe, 0xff], ""),
            (&[0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf], "\u{feff}"),
            (&[0xff, 0xfe, 0xff, 0xfe], "\u{feff}"),
            (&[0xfe, 0xff, 0xfe, 0xff], "\u{feff}"),
            (&[0xff, 0xfe, 0, 0, 0xff, 0xfe, 0, 0], "\u{feff}"),
            (&[0, 0, 0xfe, 0xff, 0, 0, 0xfe, 0xff], "\u{feff}"),
            (&[0xff, 0xfe, 0x3d, 0xd8, 0, 0xde], "😀"),
            (&[0xfe, 0xff, 0xd8, 0x3d, 0xde, 0], "😀"),
            (&[0xff, 0xfe, 0, 0, 0, 0xf6, 1, 0], "😀"),
            (&[0, 0, 0xfe, 0xff, 0, 1, 0xf6, 0], "😀"),
        ];
        for (bytes, expected) in cases {
            assert_eq!(decode_read_all_text(bytes), *expected, "{bytes:02x?}");
        }
    }
}
