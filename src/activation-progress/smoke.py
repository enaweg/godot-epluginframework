"""Exercise the published helper's startup and shutdown protocol on a desktop session."""

import base64
import os
import select
import subprocess
import sys


def start_helper(executable):
    process = subprocess.Popen(
        [executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    ready, _, _ = select.select([process.stdout], [], [], 5)
    if not ready or process.stdout.readline() != b"READY\n":
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
