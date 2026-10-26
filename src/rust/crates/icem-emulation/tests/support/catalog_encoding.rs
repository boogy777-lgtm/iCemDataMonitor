use serde_json::json;
use std::path::{Path, PathBuf};
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
pub const A: u32 = 0x13120203;
pub const B: u32 = 0x13120206;
pub const LABEL: &str = "Давление ° 😀";
#[derive(Clone, Copy, Debug)]
pub enum Encoding {
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,
}
impl Encoding {
    pub const ALL: [Self; 6] = [
        Self::Utf8,
        Self::Utf8Bom,
        Self::Utf16Le,
        Self::Utf16Be,
        Self::Utf32Le,
        Self::Utf32Be,
    ];
    pub fn encode(self, text: &str) -> Vec<u8> {
        let mut bytes = self.bom().to_vec();
        match self {
            Self::Utf8 | Self::Utf8Bom => bytes.extend_from_slice(text.as_bytes()),
            Self::Utf16Le | Self::Utf16Be => {
                for unit in text.encode_utf16() {
                    bytes.extend_from_slice(&if matches!(self, Self::Utf16Le) {
                        unit.to_le_bytes()
                    } else {
                        unit.to_be_bytes()
                    });
                }
            }
            Self::Utf32Le | Self::Utf32Be => {
                for unit in text.chars().map(u32::from) {
                    bytes.extend_from_slice(&if matches!(self, Self::Utf32Le) {
                        unit.to_le_bytes()
                    } else {
                        unit.to_be_bytes()
                    });
                }
            }
        }
        bytes
    }
    pub fn bom(self) -> &'static [u8] {
        match self {
            Self::Utf8 => &[],
            Self::Utf8Bom => &[0xef, 0xbb, 0xbf],
            Self::Utf16Le => &[0xff, 0xfe],
            Self::Utf16Be => &[0xfe, 0xff],
            Self::Utf32Le => &[0xff, 0xfe, 0, 0],
            Self::Utf32Be => &[0, 0, 0xfe, 0xff],
        }
    }
    // A malformed encoded scalar embedded in a JSON string remains valid JSON
    // after the explicit text-file replacement policy. Stream JSON rejects it.
    pub fn malformed_string(self, text: &str) -> Vec<u8> {
        assert!(text.contains('@'), "fixture marker");
        let mut bytes = self.bom().to_vec();
        let replacement: &[u8] = match self {
            Self::Utf8 | Self::Utf8Bom => &[0xff],
            Self::Utf16Le => &[0, 0xd8],
            Self::Utf16Be => &[0xd8, 0],
            Self::Utf32Le => &[0, 0, 0x11, 0],
            Self::Utf32Be => &[0, 0x11, 0, 0],
        };
        for (index, part) in text.split('@').enumerate() {
            if index > 0 {
                bytes.extend_from_slice(replacement);
            }
            let encoded = self.encode(part);
            bytes.extend_from_slice(&encoded[self.bom().len()..]);
        }
        bytes
    }
}
pub fn profile(name: &str) -> String {
    json!({"name":name,"deviceType":"FLECS","autoSelectIndices":[2],"variables":[{"registry":"p"},{"index":3,"name":"Inline","base":11,"format":"int"}]}).to_string()
}
pub fn variables(name: &str, base: f64) -> String {
    json!({"variables":[{"name":"p","display":name,"uom":"psi","simulation":{"base":base},"families":{"flecs":{"alias":name,"index":2},"lan":{"wireName":name}}}]}).to_string()
}
pub fn vectors(sv: u32, name: &str) -> String {
    json!({"serviceVectors":[{"svNumber":sv,"name":name,"equipmentType":"FLECS","signals":[{"byteOffset":4,"size":4,"valueType":"FF","name":name,"uom":"psi"}]}]}).to_string()
}
pub struct Stage {
    pub root: PathBuf,
}
impl Stage {
    pub fn new() -> Result<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "icem-catalog-encoding-{}-{nonce}",
            std::process::id()
        ));
        std::fs::create_dir_all(&root)?;
        Ok(Self { root })
    }
    pub fn directory(&self, label: impl AsRef<Path>) -> Result<PathBuf> {
        let path = self.root.join(label);
        std::fs::create_dir_all(&path)?;
        Ok(path)
    }
}
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

pub struct ByteCase {
    pub label: String,
    pub bytes: Vec<u8>,
    pub expected: String,
}
// Shared by the portable explicit-policy assertions and the BCL differential.
// A single table and exact row-count guard prevent either consumer omitting cases.
pub fn byte_cases() -> Vec<ByteCase> {
    let mut cases = Vec::new();
    for encoding in Encoding::ALL {
        cases.push(ByteCase {
            label: format!("valid-{encoding:?}"),
            bytes: encoding.encode(LABEL),
            expected: LABEL.into(),
        });
        let width = match encoding {
            Encoding::Utf8 | Encoding::Utf8Bom => 1,
            Encoding::Utf16Le | Encoding::Utf16Be => 2,
            Encoding::Utf32Le | Encoding::Utf32Be => 4,
        };
        for target in [1020, 1022, 1024] {
            let prefix = (target - encoding.bom().len()) / width;
            let text = format!("{}😀€𐐷Z", " ".repeat(prefix));
            cases.push(ByteCase {
                label: format!("boundary-{encoding:?}-{target}"),
                bytes: encoding.encode(&text),
                expected: text,
            });
        }
        if !encoding.bom().is_empty() {
            let mut bytes = encoding.bom().to_vec();
            bytes.extend(encoding.encode(LABEL));
            cases.push(ByteCase {
                label: format!("double-bom-{encoding:?}"),
                bytes,
                expected: format!("\u{feff}{LABEL}"),
            });
        }
    }
    for (label, bytes, expected) in [
        ("truncated-bom-utf8-one", vec![0xef], "�"),
        ("truncated-bom-utf8-two", vec![0xef, 0xbb], "�"),
        ("truncated-bom-utf16le", vec![0xff], "�"),
        ("truncated-bom-utf16be", vec![0xfe], "�"),
        ("truncated-bom-utf32be", vec![0, 0, 0xfe], "\0\0�"),
        ("truncated-bom-utf32le", vec![0xff, 0xfe, 0], "�"),
        ("invalid-utf8-byte", vec![b'A', 0xff, b'Z'], "A�Z"),
        ("invalid-utf8-incomplete", vec![0xe2, 0x82], "�"),
        ("invalid-utf8-overlong", vec![0xc0, 0xaf], "��"),
        ("invalid-utf8-surrogate", vec![0xed, 0xa0, 0x80], "���"),
        (
            "invalid-utf8-out-of-range",
            vec![0xf4, 0x90, 0x80, 0x80],
            "����",
        ),
        (
            "invalid-utf16le-high-tail",
            vec![0xff, 0xfe, 0, 0xd8, b'A'],
            "��",
        ),
        (
            "invalid-utf16be-high-tail",
            vec![0xfe, 0xff, 0xd8, 0, b'A'],
            "��",
        ),
        (
            "invalid-utf16le-low",
            vec![0xff, 0xfe, 0, 0xdc, b'Z', 0],
            "�Z",
        ),
        (
            "invalid-utf16be-low",
            vec![0xfe, 0xff, 0xdc, 0, 0, b'Z'],
            "�Z",
        ),
    ] {
        cases.push(ByteCase {
            label: label.into(),
            bytes,
            expected: expected.into(),
        });
    }
    for encoding in [Encoding::Utf32Le, Encoding::Utf32Be] {
        for scalar in [0xd800_u32, 0x110000] {
            for tail in 0..=3 {
                let mut bytes = encoding.bom().to_vec();
                bytes.extend_from_slice(&if matches!(encoding, Encoding::Utf32Le) {
                    scalar.to_le_bytes()
                } else {
                    scalar.to_be_bytes()
                });
                bytes.extend(std::iter::repeat_n(0x41, tail));
                cases.push(ByteCase {
                    label: format!("invalid-{encoding:?}-{scalar:x}-tail-{tail}"),
                    bytes,
                    expected: if tail == 0 {
                        "�".into()
                    } else {
                        "��".into()
                    },
                });
            }
        }
    }
    cases
}
