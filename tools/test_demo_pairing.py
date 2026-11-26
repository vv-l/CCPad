import json
from types import SimpleNamespace
import unittest
from pair_feishu_demo import match_pair


class PairingTests(unittest.TestCase):
    def message(self, **changes):
        values = dict(chat_type='p2p', create_time='101', message_type='text',
            content=json.dumps({'text': 'CCPAD-0123456789ABCDEF'}), chat_id='approved-chat')
        values.update(changes)
        return SimpleNamespace(**values)

    def test_only_fresh_exact_private_pairing_matches(self):
        code = 'CCPAD-0123456789ABCDEF'
        self.assertEqual(match_pair(self.message(), code, 100), 'approved-chat')
        for change in [dict(chat_type='group'), dict(create_time='99'), dict(message_type='image'),
                       dict(content='{"text":"hi"}'), dict(content='{invalid'), dict(chat_id='')]:
            self.assertIsNone(match_pair(self.message(**change), code, 100))


if __name__ == '__main__':
    unittest.main()
