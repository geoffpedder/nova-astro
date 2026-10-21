#!/usr/bin/env python3
"""Bluetooth for ZWO AM3/AM5/AM7 mounts: makes the mount's Bluetooth look like a serial port, so INDI's lx200am5
driver works over Bluetooth unchanged. NOVA starts and stops this itself (Mount panel: Connection > Bluetooth).

The mount (an ESP32) has one GATT characteristic for everything: commands are written to it as text and replies come
back as notifications on it. After connecting, the mount must be sent the bind command ":BS12345678#" (it answers
"OK#"); without it the mount drops the link after about 10 seconds. After that it takes the same LX200-style
commands as on its USB port.

  nova-mount-ble.py scan [--timeout S]               lists the mounts in range, one JSON line each
  nova-mount-ble.py bridge --link PATH [--name N]    connects to mount N (or the first found) and keeps a serial
                                                     port at PATH (a symlink to a pseudo-terminal) until stopped

Everything it has to say is a JSON line on stdout ({"event": ...}), which NOVA reads. It stops on SIGTERM or when
its stdin closes (NOVA gone).
"""
import argparse
import asyncio
import json
import os
import signal
import sys

SERVICE_UUID = "414d0001-6ecf-49f1-a13f-f5c2d9174f9f"
CHAR_UUID = "414d0002-6ecf-49f1-a13f-f5c2d9174f9f"
BIND_COMMAND = b":BS12345678#"
BIND_REPLY = b"OK#"
BIND_TIMEOUT = 5.0
SEARCH_TIMEOUT = 10.0
# A command the driver started but didn't end with '#' (a lone ACK byte) is sent after this long.
PARTIAL_FLUSH = 0.1

try:
    from bleak import BleakClient, BleakScanner
except ImportError:  # reported as an event, so NOVA can tell the user what to install
    BleakClient = BleakScanner = None


def emit(event, **fields):
    print(json.dumps({"event": event, **fields}), flush=True)


def is_mount(device, adv):
    """A ZWO AM mount: advertises the mount service, or (when the advertisement left the service out) is named AM…."""
    name = adv.local_name or device.name or ""
    uuids = [u.lower() for u in (adv.service_uuids or [])]
    return SERVICE_UUID in uuids or (name.startswith("AM") and "_" in name)


def mount_name(device, adv):
    return adv.local_name or device.name or device.address


async def scan(timeout):
    """One "found" line per mount, and another for the same address once its name arrives (the name is often only in
    the scan response, which comes after the first advertisement)."""
    names = {}

    def found(device, adv):
        if not is_mount(device, adv) and device.address not in names:
            return
        name = mount_name(device, adv)
        if names.get(device.address) == name or (device.address in names and name == device.address):
            return
        names[device.address] = name
        emit("found", name=name, address=device.address, rssi=adv.rssi)

    async with BleakScanner(detection_callback=found):
        await asyncio.sleep(timeout)
    emit("done", count=len(names))


class PseudoSerial:
    """A pseudo-terminal whose far end the INDI driver opens as its serial port, via a symlink at a fixed path."""

    def __init__(self, link):
        import tty

        self.link = link
        self.master, self._slave = os.openpty()
        # Raw, so nothing is echoed back or turned into line endings before the driver sets its own mode. Keeping
        # our own handle on the far end also keeps the pseudo-terminal alive while the driver closes and reopens it.
        tty.setraw(self._slave)
        os.set_blocking(self.master, False)
        self.path = os.ttyname(self._slave)
        temp = f"{link}.{os.getpid()}"
        if os.path.lexists(temp):
            os.unlink(temp)
        os.symlink(self.path, temp)
        os.replace(temp, link)

    def write(self, data):
        try:
            os.write(self.master, data)
        except BlockingIOError:
            pass  # nobody is reading (driver disconnected): drop it, as a cable would

    def close(self):
        try:
            if os.path.islink(self.link) and os.readlink(self.link) == self.path:
                os.unlink(self.link)
        except OSError:
            pass
        for fd in (self.master, self._slave):
            try:
                os.close(fd)
            except OSError:
                pass


class Bridge:
    def __init__(self, port, name=None, address=None):
        self.port = port
        self.name = name
        self.address = address
        self.commands = asyncio.Queue()
        self.connected = False
        self._partial = b""
        self._flush = None
        self._bind_reply = None

    # ---- serial side: the driver's bytes, split into commands at '#' ----
    def on_serial_readable(self):
        try:
            data = os.read(self.port.master, 4096)
        except (BlockingIOError, OSError):
            return
        if not self.connected:
            return  # no mount: the driver times out, as it would with the cable out
        self._partial += data
        while b"#" in self._partial:
            command, self._partial = self._partial.split(b"#", 1)
            self.commands.put_nowait(command + b"#")
        if self._flush:
            self._flush.cancel()
            self._flush = None
        if self._partial:
            self._flush = asyncio.get_running_loop().call_later(PARTIAL_FLUSH, self._flush_partial)

    def _flush_partial(self):
        self._flush = None
        if self._partial and self.connected:
            self.commands.put_nowait(self._partial)
        self._partial = b""

    # ---- mount side ----
    def on_notify(self, _sender, data):
        data = bytes(data)
        if self._bind_reply is not None and not self._bind_reply.done():
            self._bind_reply.set_result(data)
        else:
            self.port.write(data)

    def matches(self, device, adv):
        if not is_mount(device, adv):
            return False
        if self.address:
            return device.address.lower() == self.address.lower()
        if self.name:
            return mount_name(device, adv).lower() == self.name.lower()
        return True

    async def run(self):
        delay = 1.0
        while True:
            emit("searching", name=self.name)
            device = await BleakScanner.find_device_by_filter(self.matches, timeout=SEARCH_TIMEOUT)
            if device is None:
                continue
            emit("connecting", name=device.name or self.name, address=device.address)
            try:
                await self.session(device)
                delay = 1.0
            except asyncio.CancelledError:
                raise
            except Exception as error:  # Bleak raises several kinds; all mean "try again"
                emit("error", message=str(error) or type(error).__name__)
            self.connected = False
            emit("disconnected")
            await asyncio.sleep(delay)
            delay = min(delay * 2, 15.0)

    async def session(self, device):
        lost = asyncio.Event()
        async with BleakClient(device, disconnected_callback=lambda _client: lost.set()) as client:
            loop = asyncio.get_running_loop()
            self._bind_reply = loop.create_future()
            await client.start_notify(CHAR_UUID, self.on_notify)
            await client.write_gatt_char(CHAR_UUID, BIND_COMMAND, response=True)
            try:
                reply = await asyncio.wait_for(self._bind_reply, BIND_TIMEOUT)
            except asyncio.TimeoutError:
                raise RuntimeError("The mount didn't answer the Bluetooth bind") from None
            finally:
                self._bind_reply = None
            if reply != BIND_REPLY:
                raise RuntimeError(f"The mount refused the Bluetooth bind ({reply!r})")

            while not self.commands.empty():  # whatever the driver sent while there was no mount
                self.commands.get_nowait()
            self.connected = True
            emit("connected", name=device.name or self.name, address=device.address, tty=self.port.path)

            lost_wait = asyncio.ensure_future(lost.wait())
            try:
                while True:
                    command = asyncio.ensure_future(self.commands.get())
                    done, _ = await asyncio.wait({command, lost_wait}, return_when=asyncio.FIRST_COMPLETED)
                    if lost_wait in done:
                        command.cancel()
                        return
                    await client.write_gatt_char(CHAR_UUID, command.result(), response=True)
            finally:
                lost_wait.cancel()


async def run_bridge(link, name, address):
    port = PseudoSerial(link)
    emit("link", path=link, tty=port.path)
    bridge = Bridge(port, name, address)
    loop = asyncio.get_running_loop()
    loop.add_reader(port.master, bridge.on_serial_readable)
    try:
        await bridge.run()
    finally:
        loop.remove_reader(port.master)
        port.close()


async def main_async(args):
    task = asyncio.ensure_future(scan(args.timeout) if args.command == "scan" else run_bridge(args.link, args.name, args.address))
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            loop.add_signal_handler(sig, task.cancel)
        except (NotImplementedError, RuntimeError):
            pass  # Windows: Ctrl+C still raises KeyboardInterrupt

    if args.command == "bridge":
        # NOVA keeps our stdin open; when it closes, NOVA has gone and so do we.
        async def watch_stdin():
            reader = asyncio.StreamReader()
            await loop.connect_read_pipe(lambda: asyncio.StreamReaderProtocol(reader), sys.stdin)
            while await reader.readline():
                pass
            task.cancel()

        watcher = asyncio.ensure_future(watch_stdin())
        task.add_done_callback(lambda _t: watcher.cancel())
    try:
        await task
    except asyncio.CancelledError:
        pass


def main():
    parser = argparse.ArgumentParser(description="Bluetooth for ZWO AM mounts")
    commands = parser.add_subparsers(dest="command", required=True)
    find = commands.add_parser("scan", help="list the mounts in range")
    find.add_argument("--timeout", type=float, default=6.0)
    bridge = commands.add_parser("bridge", help="serve a mount as a serial port")
    bridge.add_argument("--link", required=True, help="where to put the serial port (a symlink)")
    bridge.add_argument("--name", help="the mount's Bluetooth name, e.g. AM3_68bc8d (default: the first found)")
    bridge.add_argument("--address", help="the mount's Bluetooth address (instead of its name)")
    args = parser.parse_args()

    if BleakClient is None:
        emit("error", message="Python's Bleak library isn't installed (python3-bleak)", fatal=True)
        return 3
    if args.command == "bridge" and not hasattr(os, "openpty"):
        emit("error", message="The Bluetooth bridge needs Linux (a pseudo-terminal for the INDI driver)", fatal=True)
        return 3
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        pass
    except Exception as error:
        emit("error", message=str(error) or type(error).__name__, fatal=True)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
