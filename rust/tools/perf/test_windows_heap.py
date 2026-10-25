"""Fail-closed checks for external ETW evidence validation; no Windows dependency."""
import unittest
from unittest.mock import Mock
from windows_heap import heap_totals, loss_counters, remove_owned_executable


class CleanupEvidence(unittest.TestCase):
    def test_locked_executable_preserves_summary_error_path(self):
        exe = Mock()
        exe.unlink.side_effect = PermissionError('file remains locked')
        errors = []
        remove_owned_executable(exe, errors)
        self.assertEqual(len(errors), 1)
        self.assertIn('file remains locked', errors[0])
        exe.unlink.assert_called_once_with(missing_ok=True)


class TraceLossEvidence(unittest.TestCase):
    def test_zero_actual_counters(self):
        self.assertEqual(loss_counters('Events Lost: 0\nLog Buffers Lost: 0\n'),
                         {'events lost': [0], 'log buffers lost': [0]})

    def test_any_session_loss_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'lost events'):
            loss_counters('Events Lost: 0\nEvents Lost: 12\nLog Buffers Lost: 0\n')

    def test_unknown_or_incomplete_evidence_rejected(self):
        for raw in ('', 'Events Lost: 0', 'Events Lost: unknown\nBuffers Lost: 0',
                    'All events fine\nBuffers Lost: 0'):
            with self.subTest(raw=raw), self.assertRaises(RuntimeError):
                loss_counters(raw)


class HeapCountEvidence(unittest.TestCase):
    def test_native_table_preserves_kb_precision(self):
        rows = heap_totals('Alloc Count,Alloc Size (KB),Heap Handle\n12,2.125,0x1234\n', True)
        self.assertEqual(rows[0]['allocation_calls'], 12)
        self.assertEqual(rows[0]['allocated_size_KB_native'], '2.125')

    def test_unknown_empty_and_zero_capture_rejected(self):
        for raw in ('PID 123 no allocation events', 'Alloc Count,Alloc Size (KB)\n',
                    'Alloc Count,Alloc Size (KB)\n0,0.000\n'):
            with self.subTest(raw=raw), self.assertRaises(RuntimeError):
                heap_totals(raw, True)

    def test_whitespace_native_table(self):
        self.assertEqual(heap_totals('Alloc number  Alloc size KB  Heap handle\n17  1.5  abc')[0]
                         ['allocation_calls'], 17)


if __name__ == '__main__':
    unittest.main()
