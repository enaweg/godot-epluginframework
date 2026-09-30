"""Exercise the published helper's startup and shutdown protocol on a desktop session."""

import base64
import os
import queue
import subprocess
import sys
import threading
import time


def start_helper(command):
    process = subprocess.Popen(
        command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    # select() only supports sockets on Windows, so read the first line on a thread instead.
    lines = queue.Queue()
    threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
    try:
        # A cold .NET start can be slow on CI runners.
        line = lines.get(timeout=20)
    except queue.Empty:
        line = None
    # Windows terminates the line with \r\n.
    if line is None or line.rstrip(b"\r\n") != b"READY":
        process.kill()
        stderr = process.communicate()[1].decode(errors="replace")
        raise AssertionError(f"helper did not become ready: {stderr}")
    return process


# The command to start the helper, e.g. `dotnet path/to/ActivationProgress.dll`.
command = sys.argv[1:]
assert os.path.isfile(command[-1])

process = start_helper(command)
try:
    # Past the helper's show delay, so the window is visible when the text changes.
    time.sleep(1)
    label = base64.b64encode("Activating sample plugin".encode())
    process.stdin.write(b"TEXT " + label + b"\nCLOSE\n")
    process.stdin.flush()
    assert process.wait(timeout=5) == 0
finally:
    if process.poll() is None:
        process.kill()

process = start_helper(command)
try:
    process.stdin.close()
    assert process.wait(timeout=5) == 0
finally:
    if process.poll() is None:
        process.kill()

# Placement arguments; each platform ignores the one it does not use.
process = start_helper(command + ["--editor-window", "0", "--editor-center", "400", "300"])
try:
    time.sleep(1)
    process.stdin.write(b"CLOSE\n")
    process.stdin.flush()
    assert process.wait(timeout=5) == 0
finally:
    if process.poll() is None:
        process.kill()

# The window has no close button, so the helper must end itself when the host never closes it.
process = start_helper(command + ["--max-lifetime-seconds", "2"])
try:
    assert process.wait(timeout=10) == 0
finally:
    if process.poll() is None:
        process.kill()

# The host never waits for READY: commands are queued before the helper has started, and a helper closed
# before its show delay must exit without ever showing its window.
process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
try:
    label = base64.b64encode("Deactivating sample plugin".encode())
    process.stdin.write(b"TEXT " + label + b"\nCLOSE\n")
    process.stdin.flush()
    # A cold .NET start can be slow on CI runners.
    assert process.wait(timeout=20) == 0
finally:
    if process.poll() is None:
        process.kill()
