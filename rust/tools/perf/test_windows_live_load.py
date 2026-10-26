import struct
import unittest

from windows_live_load import check_values, fields, read_initial_com, wait_for_initial_com, web_values


def frame(payload, flag=0):
    return bytes([flag]) + len(payload).to_bytes(4, 'big') + payload


class LiveLoadValidation(unittest.TestCase):
    def test_initial_zero_mirror_can_warm_up_before_measurement(self):
        now = [0.0]
        values = iter([0, 0, 123])
        result = wait_for_initial_com(lambda: next(values), 123, 1,
                                      lambda: now[0], lambda delay: now.__setitem__(0, now[0] + delay))
        self.assertEqual(result['attempts'], 3)
        self.assertEqual(result['initial_zero_reads'], 2)
        self.assertEqual(result['com_bits'], 123)

    def test_initial_zero_is_bounded_and_wrong_nonzero_is_never_retried(self):
        now = [0.0]
        with self.assertRaisesRegex(RuntimeError, 'startup deadline'):
            wait_for_initial_com(lambda: 0, 123, .1, lambda: now[0],
                                 lambda delay: now.__setitem__(0, now[0] + delay))
        with self.assertRaisesRegex(ValueError, 'actual=124, expected=123'):
            wait_for_initial_com(lambda: 124, 123, 1, lambda: 0)
        with self.assertRaisesRegex(ValueError, 'distinguish COM'):
            wait_for_initial_com(lambda: 0, 0, 1, lambda: 0)

    def test_late_valid_response_cannot_extend_startup_deadline(self):
        now = [0.0]
        def late_read():
            now[0] = 1.1
            return 123
        with self.assertRaisesRegex(RuntimeError, 'startup deadline'):
            wait_for_initial_com(late_read, 123, 1, lambda: now[0])

    def test_fragmented_read_uses_remaining_startup_budget(self):
        now = [0.0]
        class Connection:
            def __init__(self):
                self.response = bytearray(struct.pack('>HHHBBBHH', 0, 0, 7, 1, 3, 4, 123, 1))
                self.timeouts = []
            def settimeout(self, timeout):
                self.timeouts.append(timeout)
            def sendall(self, request):
                self.request = request
            def recv(self, count):
                now[0] += .01
                return bytes([self.response.pop(0)])
        connection = Connection()
        self.assertEqual(read_initial_com(connection, 1, lambda: now[0]), 123 | 1 << 16)
        self.assertEqual(connection.request, struct.pack('>HHHBBHH', 0, 0, 6, 1, 3, 4, 2))
        self.assertLess(connection.timeouts[-1], connection.timeouts[0])
        now[0] = 0
        connection = Connection()
        with self.assertRaisesRegex(RuntimeError, 'startup deadline'):
            read_initial_com(connection, .025, lambda: now[0])
        self.assertTrue(all(timeout <= .025 for timeout in connection.timeouts))

    def test_zero_com_during_measurement_still_fails(self):
        with self.assertRaisesRegex(ValueError, 'actual=0, expected=123'):
            check_values({4: 0}, 123)

    def test_bits_fixed_com_and_coherence(self):
        values = {4: 123, 200: 0x3f000001, 400: 0x3f000001, 402: 0x80000000, 404: 1}
        self.assertEqual(check_values(values, 123), 0x3f000001)
        for address in values:
            broken = dict(values)
            broken[address] ^= 1
            with self.assertRaises(ValueError):
                check_values(broken, 123)

    def test_grpc_frame_keeps_float_negative_zero_bits(self):
        value = b'\x08\x04\x1d' + struct.pack('<I', 0x80000000)
        payload = b'\x1a' + bytes([len(value)]) + value
        body = frame(payload) + frame(b'grpc-status: 0\r\n', 128)
        self.assertEqual(web_values(body), {4: 0x80000000})

    def test_truncated_and_unknown_wire_rejected(self):
        for wire in [b'\x00', b'\x0a\x03x', b'\x08\x80', b'\x0b']:
            with self.assertRaises(ValueError):
                list(fields(wire))
        with self.assertRaises(ValueError):
            web_values(frame(b'')[:-1])

    def test_missing_failed_or_duplicate_unary_rejected(self):
        for body in [frame(b''), frame(b'') + frame(b'grpc-status: 13', 128),
                     frame(b'') + frame(b'') + frame(b'grpc-status:0', 128)]:
            with self.assertRaises(ValueError):
                web_values(body)


if __name__ == '__main__':
    unittest.main()
