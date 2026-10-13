"""Exercise the published helper's startup and shutdown protocol on a desktop session."""

import base64
import os
import queue
import subprocess
import sys
import threading
import time
import tempfile


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

# Separate editor processes append to one command channel. Closing each writer (even before the
# helper starts) must leave the same helper alive through two restarts, until the final CLOSE.
with tempfile.TemporaryDirectory() as directory:
    command_file = os.path.join(directory, "progress.commands")
    open(command_file, "w").close()
    process = subprocess.Popen(
        command + ["--command-file", command_file, "--max-lifetime-seconds", "5"],
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    try:
        for _ in range(3):
            subprocess.run(
                [sys.executable, "-c",
                 "import sys; f=open(sys.argv[1], 'a'); f.write('HEARTBEAT\\nTEXT V29ya2luZy4uLg==\\n'); f.close()",
                 command_file], check=True
            )
            time.sleep(1)
            assert process.poll() is None, "helper exited when an editor writer closed"
        # A partial command must wait for its newline, including when the next editor appends it.
        with open(command_file, "a") as writer:
            writer.write("CLO")
        time.sleep(0.2)
        assert process.poll() is None
        with open(command_file, "a") as writer:
            writer.write("SE\n")
        # CLOSE ends the anonymous user; the helper exits once its lifetime has passed since that last message.
        assert process.wait(timeout=15) == 0
    finally:
        if process.poll() is None:
            process.kill()

    # An abandoned restart retains the same bounded lifetime as a stalled editor operation.
    open(command_file, "w").close()
    process = subprocess.Popen(
        command + ["--command-file", command_file, "--max-lifetime-seconds", "2"],
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    try:
        assert process.wait(timeout=10) == 0
    finally:
        if process.poll() is None:
            process.kill()

# Several users share one helper through its command file. The window follows them, but the helper keeps running until
# no user has sent anything for its lifetime; then it withdraws its record (<token>.helper), exits and removes its files.
with tempfile.TemporaryDirectory() as directory:
    command_file = os.path.join(directory, "token.commands")
    record = os.path.join(directory, "token.helper")
    open(command_file, "w").close()
    open(record, "w").close()

    def send(text):
        with open(command_file, "a") as writer:
            writer.write(text)

    process = subprocess.Popen(
        command + ["--command-file", command_file, "--max-lifetime-seconds", "4", "--window-timeout-seconds", "1"],
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    try:
        send("first TEXT QQ==\nsecond TEXT Qg==\n")
        for _ in range(3):
            time.sleep(1)
            send("first HEARTBEAT\nsecond HOLD 30\n")
        send("first END\n")
        # Both users are still known: the helper outlives the window until its lifetime has passed since the last message.
        time.sleep(2)
        assert process.poll() is None, "helper exited while a user holds it"
        send("second END\n")
        assert process.wait(timeout=15) == 0
        assert sorted(os.listdir(directory)) == [], os.listdir(directory)
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

# Each HEARTBEAT restarts the lifetime, so a long chain of plugin installs keeps the window; without more
# heartbeats it still ends on its own.
process = start_helper(command + ["--max-lifetime-seconds", "3"])
try:
    # Outlives the 3 s lifetime only if the heartbeats restart it.
    for _ in range(5):
        time.sleep(1)
        process.stdin.write(b"HEARTBEAT\n")
        process.stdin.flush()
    assert process.poll() is None, "helper exited despite heartbeats"
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
