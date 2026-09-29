"""Exercise the published helper's startup and shutdown protocol on a desktop session."""

import base64
import os
import queue
import subprocess
import sys
import threading


def start_helper(executable):
    process = subprocess.Popen(
        [executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    # select() only supports sockets on Windows, so read the first line on a thread instead.
    lines = queue.Queue()
    threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
    try:
        # CI can take longer on the first launch while the single-file bundle extracts.
        line = lines.get(timeout=20)
    except queue.Empty:
        line = None
    # Windows terminates the line with \r\n.
    if line is None or line.rstrip(b"\r\n") != b"READY":
        process.kill()
        stderr = process.communicate()[1].decode(errors="replace")
        raise AssertionError(f"helper did not become ready: {stderr}")
    return process


executable = sys.argv[1]
assert os.path.isfile(executable)

process = start_helper(executable)
try:
    label = base64.b64encode("Activating sample plugin".encode())
    process.stdin.write(b"TEXT " + label + b"\nCLOSE\n")
    process.stdin.flush()
    assert process.wait(timeout=5) == 0
finally:
    if process.poll() is None:
        process.kill()

process = start_helper(executable)
try:
    process.stdin.close()
    assert process.wait(timeout=5) == 0
finally:
    if process.poll() is None:
        process.kill()
