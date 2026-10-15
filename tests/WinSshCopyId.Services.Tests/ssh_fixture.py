"""Loopback-only SSH protocol fixture with a deliberately limited command emulator.

No OS accounts, real credentials, shell execution, services or firewall changes.
All host keys and authorized client keys exist only for this process.
"""
import json
import logging
import pathlib
import os
import re
import socket
import sys
import threading
import time
import paramiko

logging.getLogger("paramiko").addHandler(logging.NullHandler())
logging.getLogger("paramiko").propagate = False
root = pathlib.Path(sys.argv[1]).resolve()
control = root / "control.json"
events = root / "events.jsonl"
keys = [paramiko.RSAKey.generate(2048), paramiko.RSAKey.generate(2048)]
authorized = set()
lock = threading.Lock()

def event(kind):
    with lock:
        with events.open("a", encoding="utf-8") as output:
            output.write(json.dumps({"event": kind}) + "\n")

def config():
    try:
        return json.loads(control.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}

class Server(paramiko.ServerInterface):
    def __init__(self, mode):
        self.mode = mode
        self.command = None
        self.ready = threading.Event()
    def get_allowed_auths(self, username):
        return "publickey,password"
    def check_auth_password(self, username, password):
        event("password-auth")
        return paramiko.AUTH_SUCCESSFUL if username == "fixture" and password == "fixture-only-not-a-user-credential" else paramiko.AUTH_FAILED
    def check_auth_publickey(self, username, key):
        event("publickey-auth")
        with lock:
            accepted = key.get_name() + " " + key.get_base64() in authorized
        return paramiko.AUTH_SUCCESSFUL if username == "fixture" and accepted else paramiko.AUTH_FAILED
    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_exec_request(self, channel, command):
        self.command = command.decode("utf-8")
        self.ready.set()
        return True

def serve(client):
    settings = config()
    transport = paramiko.Transport(client)
    transport.add_server_key(keys[settings.get("key", 0)])
    server = Server(settings.get("mode", "normal"))
    try:
        transport.start_server(server=server)
        channel = transport.accept(10)
        if channel is None or not server.ready.wait(10):
            return
        if server.mode == "stall":
            event("command-stalled")
            deadline = time.monotonic() + 35
            while transport.is_active() and time.monotonic() < deadline:
                time.sleep(0.05)
            return
        command = server.command
        if command == "echo WINSSHCOPYID_OK":
            event("verify-command")
            channel.send(b"WINSSHCOPYID_OK\n" if server.mode != "verify-fail" else b"NO\n")
        elif command.startswith("set -e; umask 077; "):
            match = re.search(r"KEY='([^']*)';", command)
            if match is None:
                channel.send_exit_status(2)
                return
            line = " ".join(match.group(1).split(" ")[:2])
            with lock:
                already = line in authorized
                authorized.add(line)
            event("duplicate-install" if already else "installed")
            channel.send(b"ALREADY_PRESENT\n" if already else b"ADDED\n")
        else:
            channel.send_exit_status(127)
            return
        channel.send_exit_status(0)
        channel.shutdown_write()
        time.sleep(0.05)
        channel.close()
    except (EOFError, OSError, paramiko.SSHException):
        event("connection-ended")
    finally:
        transport.close()

listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen(16)
listener.settimeout(0.25)
(root / "ready.json").write_text(json.dumps({"port": listener.getsockname()[1], "engine": "Paramiko", "version": paramiko.__version__, "processId": os.getpid()}), encoding="utf-8")
deadline = time.monotonic() + 120
try:
    while time.monotonic() < deadline:
        try:
            client, address = listener.accept()
        except socket.timeout:
            continue
        threading.Thread(target=serve, args=(client,), daemon=True).start()
finally:
    listener.close()
