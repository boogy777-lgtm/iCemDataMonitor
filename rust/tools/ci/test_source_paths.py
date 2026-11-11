"""Keep literal Rust/oracle inputs attached to their actual source owners."""
import re
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
RUST = REPO / "rust"
INCLUDE = re.compile(r'include_(?:str|bytes)!\(\s*"([^"\r\n]+)"\s*\)')
PS_ROOT = re.compile(r"Join-Path\s+\$RepoRoot\s+'([^']+)'", re.IGNORECASE)
CS_ROOT = re.compile(r'Path\.Combine\(\s*root\s*((?:,\s*"[^"\r\n]+")+)\s*\)')


def literal_inputs(source: Path, text: str) -> list[Path]:
    if source.suffix == ".rs":
        return [source.parent / name for name in INCLUDE.findall(text)]
    if source.suffix == ".ps1":
        return [REPO / name for name in PS_ROOT.findall(text)]
    if source.suffix == ".cs":
        return [REPO.joinpath(*re.findall(r'"([^"\r\n]+)"', args)) for args in CS_ROOT.findall(text)]
    return []


class SourcePathsTests(unittest.TestCase):
    def test_literal_inputs_exist(self):
        sources = [p for root in (RUST / "apps", RUST / "crates", RUST / "tests")
                   for p in root.rglob("*.rs")]
        sources += list((RUST / "tests/support").glob("*.ps1"))
        sources += list((RUST / "tests/support").glob("*.cs"))
        inputs = [(source, target) for source in sources
                  for target in literal_inputs(source, source.read_text(encoding="utf-8"))]
        self.assertTrue(inputs, "literal-input discovery must not be empty")
        missing = [f"{source.relative_to(REPO)}: {target}" for source, target in inputs
                   if not target.is_file() and not target.is_dir()]
        self.assertEqual([], missing, "missing literal source/oracle inputs")

    def test_discovery_catches_missing_inputs_in_each_supported_form(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "test.rs"
            paths = literal_inputs(source, 'include_str!("absent.cs"); include_bytes!("absent.bin")')
            self.assertEqual(2, len(paths))
            self.assertTrue(all(not path.exists() for path in paths))
        for suffix, text in [
            (".ps1", "Join-Path $RepoRoot 'missing-source-guard/input.cs'"),
            (".cs", 'Path.Combine(root,"missing-source-guard","input.json")'),
        ]:
            paths = literal_inputs(Path("test" + suffix), text)
            self.assertEqual(1, len(paths))
            self.assertFalse(paths[0].exists())


if __name__ == "__main__":
    unittest.main()
