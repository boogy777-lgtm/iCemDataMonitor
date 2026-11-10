"""CI text inputs are UTF-8 regardless of the Windows code page."""
import ast
from pathlib import Path
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


class EncodingTests(unittest.TestCase):
    def test_ci_text_io_has_explicit_utf8(self):
        for path in Path(__file__).resolve().parent.glob('*.py'):
            with self.subTest(path=path.name):
                self.assertEqual(implicit_text_calls(path.read_bytes()), [], path.name)

    def test_guard_rejects_implicit_and_wrong_encodings(self):
        source = "p.read_text()\np.write_text('x')\np.read_text(encoding='cp1251')\n"
        self.assertEqual(implicit_text_calls(source), [(1, 'read_text'), (2, 'write_text'), (3, 'read_text')])
        self.assertEqual(implicit_text_calls("p.read_text(encoding='utf-8-sig')\np.write_text('x', encoding='utf-8')"), [])


if __name__ == '__main__':
    unittest.main()
