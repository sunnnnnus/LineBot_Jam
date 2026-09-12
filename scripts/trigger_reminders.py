"""Trigger the authenticated queue endpoint. No DB/LINE/Groq credentials are needed."""
import http.client
import json
import os
import time
import uuid
from urllib.parse import urlsplit


def trigger(base_url, token, *, connect=http.client.HTTPSConnection, sleep=time.sleep, run_id=None):
    target = urlsplit(base_url)
    if (target.scheme != "https" or not target.hostname or target.username or target.password
            or target.query or target.fragment or target.path not in ("", "/")):
        raise ValueError("REMINDER_BASE_URL must be an HTTPS service origin without credentials or a path")
    if not token or "\r" in token or "\n" in token:
        raise ValueError("REMINDER_SCHEDULER_TOKEN is missing or invalid")
    run_id = run_id or str(uuid.uuid4())
    for attempt in range(3):
        connection = connect(target.hostname, target.port or 443, timeout=120)
        status = None
        accepted = False
        try:
            connection.request("POST", "/api/reminder/process", body=b"", headers={
                "Authorization": "Bearer " + token,
                "X-Scheduler-Run-Id": run_id,
                "Content-Type": "application/json",
            })
            response = connection.getresponse()
            status = response.status
            body = response.read(65536)
            if status == 202:
                try:
                    result = json.loads(body)
                    accepted = (result.get("status") == "accepted"
                                and result.get("requestId") == "reminder-scan:" + run_id)
                except (ValueError, AttributeError):
                    pass
        except (OSError, http.client.HTTPException):
            pass
        finally:
            connection.close()
        if accepted:
            print("Reminder scan accepted into the durable queue.")
            return
        # Never follow redirects with the bearer token, or repeatedly retry bad credentials.
        if status is not None and 300 <= status < 500 and status != 429:
            raise RuntimeError(f"Scheduler rejected (HTTP {status}); check URL/token configuration")
        if attempt < 2:
            print("Service not ready; retrying after cold start.")
            sleep(10 * (attempt + 1))
    raise RuntimeError("Reminder scan was not acknowledged after 3 attempts")


if __name__ == "__main__":
    trigger(os.environ.get("REMINDER_BASE_URL", ""), os.environ.get("REMINDER_SCHEDULER_TOKEN", ""))
