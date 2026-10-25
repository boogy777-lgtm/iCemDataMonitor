import struct
import unittest

from windows_live_load import check_values, fields, web_values


def frame(payload, flag=0):
    return bytes([flag]) + len(payload).to_bytes(4, 'big') + payload


class LiveLoadValidation(unittest.TestCase):
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
