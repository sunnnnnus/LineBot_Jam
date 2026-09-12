import unittest
from trigger_reminders import trigger


class TriggerTests(unittest.TestCase):
    def test_cold_start_then_accepts_with_same_key(self):
        requests = []
        responses = [TimeoutError(), (202, b'{"status":"accepted","requestId":"reminder-scan:fixed"}')]

        class Connection:
            def __init__(self, *args, **kwargs):
                self.timeout = kwargs["timeout"]
            def request(self, method, path, **kwargs):
                requests.append((method, path, kwargs["headers"], self.timeout))
            def getresponse(self):
                result = responses.pop(0)
                if isinstance(result, Exception):
                    raise result
                class Response:
                    status = result[0]
                    def read(self, _): return result[1]
                return Response()
            def close(self): pass

        trigger("https://example.onrender.com", "test-token", connect=Connection, sleep=lambda _: None, run_id="fixed")
        self.assertEqual(2, len(requests))
        self.assertEqual(requests[0][2]["X-Scheduler-Run-Id"], requests[1][2]["X-Scheduler-Run-Id"])
        self.assertEqual(120, requests[0][3])

    def test_redirect_does_not_forward_secret(self):
        class Connection:
            def __init__(self, *args, **kwargs): pass
            def request(self, *args, **kwargs): pass
            def getresponse(self): return self
            status = 302
            def read(self, _): return b""
            def close(self): pass
        with self.assertRaisesRegex(RuntimeError, "302"):
            trigger("https://example.onrender.com", "token", connect=Connection)

    def test_plain_http_is_rejected(self):
        with self.assertRaises(ValueError):
            trigger("http://example.onrender.com", "token")

    def test_loading_page_is_not_success(self):
        class Connection:
            def __init__(self, *args, **kwargs): pass
            def request(self, *args, **kwargs): pass
            def getresponse(self): return self
            status = 200
            def read(self, _): return b"<html>Starting...</html>"
            def close(self): pass
        with self.assertRaisesRegex(RuntimeError, "3 attempts"):
            trigger("https://example.onrender.com", "token", connect=Connection, sleep=lambda _: None)


if __name__ == "__main__":
    unittest.main()
