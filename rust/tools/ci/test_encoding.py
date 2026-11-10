"""CI text inputs are UTF-8 regardless of the Windows code page."""
import ast
from pathlib import Path
import tempfile
import unittest


def implicit_text_calls(source):
    tree = ast.parse(source)
    return sorted((node.lineno, node.func.attr) for node in ast.walk(tree)
                  if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                  and node.func.attr in ('read_text', 'write_text')
                  and not any(keyword.arg == 'encoding'
                              and isinstance(keyword.value, ast.Constant)
                              and keyword.value.value in ('utf-8', 'utf-8-sig')
                              for keyword in node.keywords))


def ci_source_files(root):
    roots = (root / 'tools/ci', root / 'rust/tools/ci')
    return (path for directory in roots for path in sorted(directory.glob('*.py')))


def ci_text_io_report(root):
    return {path: implicit_text_calls(path.read_bytes()) for path in ci_source_files(root)}


class EncodingTests(unittest.TestCase):
    def test_ci_text_io_has_explicit_utf8(self):
        root = Path(__file__).resolve().parents[3]
        report = ci_text_io_report(root)
        self.assertTrue(any(path.parent == root / 'tools/ci' for path in report))
        self.assertTrue(any(path.parent == root / 'rust/tools/ci' for path in report))
        self.assertTrue(all(not calls for calls in report.values()))

    def test_scan_finds_implicit_io_in_a_new_common_source(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            common = root / 'tools/ci'
            rust = root / 'rust/tools/ci'
            common.mkdir(parents=True)
            rust.mkdir(parents=True)
            (common / 'new_common_check.py').write_text(
                "Path('input').read_text()\nPath('output').write_text('x', encoding='cp1251')\n",
                encoding='utf-8')
            (rust / 'rust_check.py').write_text("Path('input').read_text(encoding='utf-8')\n", encoding='utf-8')

            report = {path.relative_to(root).as_posix(): calls
                      for path, calls in ci_text_io_report(root).items()}
            self.assertEqual(report, {
                'tools/ci/new_common_check.py': [(1, 'read_text'), (2, 'write_text')],
                'rust/tools/ci/rust_check.py': [],
            })

    def test_guard_rejects_implicit_and_wrong_encodings(self):
        source = "p.read_text()\np.write_text('x')\np.read_text(encoding='cp1251')\n"
        self.assertEqual(implicit_text_calls(source), [(1, 'read_text'), (2, 'write_text'), (3, 'read_text')])
        self.assertEqual(implicit_text_calls("p.read_text(encoding='utf-8-sig')\np.write_text('x', encoding='utf-8')"), [])


if __name__ == '__main__':
    unittest.main()
