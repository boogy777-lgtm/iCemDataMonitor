"""Fail-closed checks for external ETW evidence validation; no Windows dependency."""
import unittest
from pathlib import Path
from unittest.mock import Mock
from windows_heap import existing_profile_loggers, heap_totals, loss_counters, remove_owned_executable


class LoggerInventoryEvidence(unittest.TestCase):
    def test_actual_defender_lowpriv_name_is_not_wpr(self):
        self.assertEqual(existing_profile_loggers(
            'Logger Name           : DefenderApiLoggerLowPriv\n'
            'Events Lost           : 2\nLogger Name : RTEvents\n'), [])

    def test_existing_profile_sessions_still_rejected(self):
        self.assertEqual(existing_profile_loggers(
            'Logger Name : WPR_initiated_Heap\nLogger Name : HeapSession\n'),
            ['WPR_initiated_Heap', 'HeapSession'])

    def test_unknown_inventory_fails_closed(self):
        for text in ('', 'No known logger fields', 'Logger Name :\n',
                     'Logger Name :\nEvents Lost : 0\n'):
            with self.assertRaises(RuntimeError):
                existing_profile_loggers(text)


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
    def test_actual_dumper_trace_header(self):
        raw = (Path(__file__).parent / 'fixtures/run40192653339-dumper-excerpt.csv').read_text()
        self.assertEqual(loss_counters(raw), {'events lost': [0], 'buffers lost': [0]})
        with self.assertRaisesRegex(RuntimeError, 'lost events'):
            loss_counters(raw.replace('Events Lost: 0', 'Events Lost: 1'))
        with self.assertRaisesRegex(RuntimeError, 'unknown ETW loss'):
            loss_counters(raw.replace('Buffers lost: 0', 'Buffers lost: unknown'))

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
    def test_actual_xperf_total_and_per_heap_rows_are_not_summed(self):
        raw = (Path(__file__).parent / 'fixtures/run40192653339-heap-totals.txt').read_text()
        rows = heap_totals(raw, True)
        self.assertEqual(len(rows), 4)
        self.assertEqual(rows[0]['allocation_calls'], 783)
        self.assertEqual(rows[0]['allocated_size_KB_native'], '222.7')
        self.assertEqual(rows[0]['native_row'][-1], 'TOTAL')
        self.assertEqual(rows[1]['allocation_calls'], 773)

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
