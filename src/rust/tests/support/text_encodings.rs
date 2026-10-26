//! Test-only operator file encodings shared by Host catalog boundary checks.
pub fn encoded_texts(text: &str) -> Vec<Vec<u8>> {
    let mut files = vec![
        text.as_bytes().to_vec(),
        [vec![0xef, 0xbb, 0xbf], text.as_bytes().to_vec()].concat(),
    ];
    for little in [true, false] {
        let mut bytes = if little {
            vec![0xff, 0xfe]
        } else {
            vec![0xfe, 0xff]
        };
        for unit in text.encode_utf16() {
            bytes.extend_from_slice(&if little {
                unit.to_le_bytes()
            } else {
                unit.to_be_bytes()
            });
        }
        files.push(bytes);
    }
    for little in [true, false] {
        let mut bytes = if little {
            vec![0xff, 0xfe, 0, 0]
        } else {
            vec![0, 0, 0xfe, 0xff]
        };
        for scalar in text.chars().map(u32::from) {
            bytes.extend_from_slice(&if little {
                scalar.to_le_bytes()
            } else {
                scalar.to_be_bytes()
            });
        }
        files.push(bytes);
    }
    files
}
