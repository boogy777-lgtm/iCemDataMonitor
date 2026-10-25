import unittest

from windows_heap_window import event_totals


class WindowHeapValidation(unittest.TestCase):
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
