import unittest
from pathlib import Path
import contextlib
import io
import json
import tempfile
from unittest.mock import Mock, patch

import windows_heap_window
from windows_heap_window import event_totals


class OwnedProcessMetadataEvidence(unittest.TestCase):
    def test_child_launch_failure_stops_only_same_wpr_instance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / 'tool.exe'
            executable.touch()
            commands = []

            def command(argv, **kwargs):
                commands.append(argv)
                output = 'Logger Name : DefenderApiLoggerLowPriv\n' if '-loggers' in argv else 'recording'
                return Mock(returncode=0, stdout=output)

            with patch('sys.argv', ['heap-window', '--output', directory]), \
                 patch.object(windows_heap_window.platform, 'system', return_value='Windows'), \
                 patch.object(windows_heap_window.shutil, 'which', return_value=str(executable)), \
                 patch.object(windows_heap_window.subprocess, 'run', side_effect=command), \
                 patch.object(windows_heap_window.subprocess, 'Popen', side_effect=OSError('launch failed')), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(windows_heap_window.main(), 1)
            recording = [argv for argv in commands if any(flag in argv for flag in ('-start', '-status', '-stop'))]
            self.assertEqual(len(recording), 3)
            instance = recording[0][-1]
            self.assertTrue(instance.startswith('icem_process_'))
            self.assertTrue(all(argv[-2:] == ['-instancename', instance] for argv in recording))
            self.assertIn('-stop', recording[-1])
            report = json.loads((root / 'heap-window-summary.json').read_text())
            self.assertEqual(report['cleanup_errors'], [])
            self.assertIn('launch failed', report['error'])

    def test_child_cleanup_failure_preserves_wpr_stop_and_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / 'tool.exe'
            executable.touch()
            commands = []
            process = Mock(pid=42)
            process.kill.side_effect = OSError('kill failed')

            def command(argv, **kwargs):
                commands.append(argv)
                output = 'Logger Name : DefenderApiLoggerLowPriv\n' if '-loggers' in argv else 'recording'
                return Mock(returncode=0, stdout=output)

            with patch('sys.argv', ['heap-window', '--output', directory]), \
                 patch.object(windows_heap_window.platform, 'system', return_value='Windows'), \
                 patch.object(windows_heap_window.shutil, 'which', return_value=str(executable)), \
                 patch.object(windows_heap_window.subprocess, 'run', side_effect=command), \
                 patch.object(windows_heap_window.subprocess, 'Popen', return_value=process), \
                 patch.object(windows_heap_window, 'read_protocol', side_effect=RuntimeError('handshake failed')), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(windows_heap_window.main(), 1)
            self.assertIn('-stop', commands[-1])
            self.assertEqual(commands[-1][-2], '-instancename')
            report = json.loads((Path(directory) / 'heap-window-summary.json').read_text())
            self.assertEqual(report['cleanup_errors'], ['child stop: kill failed'])
            self.assertIn('handshake failed', report['error'])
            process.stdin.close.assert_called_once()
            process.stdout.close.assert_called_once()


class WindowHeapValidation(unittest.TestCase):
    def test_actual_unresolved_pid_is_never_assigned_to_target(self):
        raw = (Path(__file__).parent / 'fixtures/run40192653339-dumper-excerpt.csv').read_text()
        with self.assertRaisesRegex(ValueError, 'unknown process name/PID'):
            event_totals(raw, 8612)

    def test_exact_bytes_pid_and_reallocation_are_separate(self):
        text = ('HeapAlloc,TimeStamp,ProcessID,HeapHandle,Size,Address\n'
                'HeapAlloc,1,42,0xab,0x28,0x10\n'
                'HeapAlloc,2,55,0xab,999,0x20\n'
                'HeapAlloc,3,42,0xab,8,0x30\n'
                'HeapRealloc,TimeStamp,PID,OldSize,NewSize\n'
                'HeapRealloc,4,42,10,16\n')
        self.assertEqual(event_totals(text, 42), {'allocation_calls': 2, 'allocated_bytes': 48,
                         'reallocation_calls': 1, 'reallocated_new_bytes': 16})

    def test_process_name_pid_header(self):
        text = ('HeapAlloc,TimeStamp,Process Name ( PID),Size\n'
                'HeapAlloc,1,icem-host ( 42),64\n')
        self.assertEqual(event_totals(text, 42)['allocated_bytes'], 64)

    def test_unknown_schema_missing_pid_or_kb_rejected(self):
        for text in ['HeapAlloc,TimeStamp,Size\nHeapAlloc,1,64\n',
                     'HeapAlloc,TimeStamp,PID,SizeKB\nHeapAlloc,1,42,64\n',
                     'HeapAlloc,1,42,64\n',
                     'HeapAlloc,TimeStamp,PID,Size\nHeapAlloc,1,42,4.00\n']:
            with self.assertRaises(ValueError):
                event_totals(text, 42)

    def test_zero_control_requires_actual_header(self):
        header = 'HeapAlloc,TimeStamp,PID,Size\n'
        self.assertEqual(event_totals(header, 42, False)['allocation_calls'], 0)
        with self.assertRaises(ValueError):
            event_totals('', 42, False)
        with self.assertRaises(ValueError):
            event_totals(header, 42, True)


if __name__ == '__main__':
    unittest.main()
