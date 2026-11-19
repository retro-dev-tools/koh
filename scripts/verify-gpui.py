import argparse
import ctypes as c
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time
import zlib


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def write_png(path, rgba, width, height):
    rows = b"".join(b"\0" + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + png_chunk(b"IDAT", zlib.compress(rows)) + png_chunk(b"IEND", b""))


def read_oracle(path):
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    offset, compressed = 8, bytearray()
    while offset < len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        chunk = data[offset + 8:offset + 8 + length]
        assert zlib.crc32(kind + chunk) == struct.unpack_from(">I", data, offset + 8 + length)[0]
        if kind == b"IHDR":
            assert struct.unpack(">IIBBBBB", chunk) == (160, 144, 8, 6, 0, 0, 0)
        if kind == b"IDAT":
            compressed.extend(chunk)
        offset += length + 12
    rows = zlib.decompress(compressed)
    assert len(rows) == 144 * 641
    assert all(rows[y * 641] == 0 for y in range(144))
    rgba = b"".join(rows[y * 641 + 1:(y + 1) * 641] for y in range(144))
    assert rgba[3::4] == b"\xff" * (160 * 144)
    return rgba


class Attributes(c.Structure):
    _fields_ = [
        ("x", c.c_int), ("y", c.c_int), ("width", c.c_int), ("height", c.c_int),
        ("border", c.c_int), ("depth", c.c_int), ("visual", c.c_void_p),
        ("root", c.c_ulong), ("class_", c.c_int), ("bit_gravity", c.c_int),
        ("win_gravity", c.c_int), ("backing_store", c.c_int),
        ("backing_planes", c.c_ulong), ("backing_pixel", c.c_ulong),
        ("save_under", c.c_int), ("colormap", c.c_ulong), ("map_installed", c.c_int),
        ("map_state", c.c_int), ("all_events", c.c_long), ("your_events", c.c_long),
        ("propagate", c.c_long), ("override_redirect", c.c_int), ("screen", c.c_void_p),
    ]


class XImage(c.Structure):
    _fields_ = [
        ("width", c.c_int), ("height", c.c_int), ("xoffset", c.c_int),
        ("format", c.c_int), ("data", c.c_void_p), ("byte_order", c.c_int),
        ("bitmap_unit", c.c_int), ("bitmap_bit_order", c.c_int), ("bitmap_pad", c.c_int),
        ("depth", c.c_int), ("bytes_per_line", c.c_int), ("bits_per_pixel", c.c_int),
        ("red_mask", c.c_ulong), ("green_mask", c.c_ulong), ("blue_mask", c.c_ulong),
    ]


class ClientMessage(c.Structure):
    _fields_ = [
        ("type", c.c_int), ("serial", c.c_ulong), ("send_event", c.c_int),
        ("display", c.c_void_p), ("window", c.c_ulong), ("message_type", c.c_ulong),
        ("format", c.c_int), ("data", c.c_long * 5),
    ]


class Event(c.Union):
    _fields_ = [("client", ClientMessage), ("pad", c.c_long * 24)]


def main():
    parser = argparse.ArgumentParser(description="Native X11 check using the SDK 2048-v2 ROM")
    parser.add_argument("desktop", type=Path)
    parser.add_argument("rom", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    desktop, output = args.desktop.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    rom = output / "2048.gb"
    shutil.copyfile(args.rom, rom)
    engine = desktop.with_name("Koh.Emulator.App")
    assert engine.is_file(), "windowless engine must be beside the desktop"
    environment = dict(os.environ, ALSOFT_DRIVERS="null", GSETTINGS_BACKEND="memory")
    for name in ["DISPLAY", "WAYLAND_DISPLAY", "KOH_ENGINE"]:
        environment.pop(name, None)
    with (output / "oracle.log").open("w") as log:
        subprocess.run([str(engine), str(rom), "--headless", "--frames=480", "--screenshot=" + str(output / "oracle.png")], env=environment, stdout=log, stderr=log, check=True, timeout=30)
    oracle = read_oracle(output / "oracle.png")
    x, xt = c.CDLL("libX11.so.6"), c.CDLL("libXtst.so.6")
    signatures = [
        (x, "XOpenDisplay", [c.c_char_p], c.c_void_p),
        (x, "XDefaultRootWindow", [c.c_void_p], c.c_ulong),
        (x, "XQueryTree", [c.c_void_p, c.c_ulong, c.POINTER(c.c_ulong), c.POINTER(c.c_ulong), c.POINTER(c.POINTER(c.c_ulong)), c.POINTER(c.c_uint)], c.c_int),
        (x, "XFetchName", [c.c_void_p, c.c_ulong, c.POINTER(c.c_char_p)], c.c_int),
        (x, "XFree", [c.c_void_p], c.c_int),
        (x, "XGetWindowAttributes", [c.c_void_p, c.c_ulong, c.POINTER(Attributes)], c.c_int),
        (x, "XGetImage", [c.c_void_p, c.c_ulong, c.c_int, c.c_int, c.c_uint, c.c_uint, c.c_ulong, c.c_int], c.POINTER(XImage)),
        (x, "XDestroyImage", [c.POINTER(XImage)], c.c_int),
        (x, "XStringToKeysym", [c.c_char_p], c.c_ulong),
        (x, "XKeysymToKeycode", [c.c_void_p, c.c_ulong], c.c_uint),
        (x, "XSetInputFocus", [c.c_void_p, c.c_ulong, c.c_int, c.c_ulong], c.c_int),
        (x, "XFlush", [c.c_void_p], c.c_int),
        (x, "XCloseDisplay", [c.c_void_p], c.c_int),
        (x, "XInternAtom", [c.c_void_p, c.c_char_p, c.c_int], c.c_ulong),
        (x, "XSendEvent", [c.c_void_p, c.c_ulong, c.c_int, c.c_long, c.POINTER(Event)], c.c_int),
        (xt, "XTestFakeKeyEvent", [c.c_void_p, c.c_uint, c.c_int, c.c_ulong], c.c_int),
    ]
    for library, name, arguments, result in signatures:
        function = getattr(library, name)
        function.argtypes, function.restype = arguments, result
    read_fd, write_fd = os.pipe()
    server = app = connection = None
    try:
        with (output / "xvfb.log").open("w") as log:
            server = subprocess.Popen(["Xvfb", "-displayfd", str(write_fd), "-screen", "0", "1280x800x24", "-nolisten", "tcp"], pass_fds=[write_fd], stdout=log, stderr=log)
        os.close(write_fd)
        with os.fdopen(read_fd) as reader:
            display = reader.readline().strip()
        assert display and server.poll() is None, "Xvfb did not start"
        environment["DISPLAY"] = ":" + display
        connection = x.XOpenDisplay(environment["DISPLAY"].encode())
        assert connection
        root = x.XDefaultRootWindow(connection)
        def spawn_window(arguments, log_name):
            nonlocal app
            with (output / log_name).open("w") as log:
                app = subprocess.Popen([str(desktop), *arguments], env=environment, stdout=log, stderr=log)
            window, deadline = None, time.monotonic() + 30
            while window is None:
                assert app.poll() is None, "desktop exited before mapping"
                parent, unused, children, count = c.c_ulong(), c.c_ulong(), c.POINTER(c.c_ulong)(), c.c_uint()
                x.XQueryTree(connection, root, c.byref(unused), c.byref(parent), c.byref(children), c.byref(count))
                for index in range(count.value):
                    name = c.c_char_p()
                    x.XFetchName(connection, children[index], c.byref(name))
                    attributes = Attributes()
                    if name.value == b"Koh Emulator" and x.XGetWindowAttributes(connection, children[index], c.byref(attributes)) and attributes.map_state == 2:
                        window = children[index]
                    if name:
                        x.XFree(name)
                x.XFree(children)
                assert time.monotonic() < deadline, "window mapping timed out"
                time.sleep(.05)
            return window

        window = spawn_window([str(rom)], "desktop.log")
        def capture(name):
            image = x.XGetImage(connection, root, 0, 0, 1280, 800, 0xffffffffffffffff, 2)
            assert image
            info = image.contents
            assert (info.bits_per_pixel, info.byte_order, info.bytes_per_line) == (32, 0, 1280 * 4)
            raw = c.string_at(info.data, info.bytes_per_line * info.height)
            x.XDestroyImage(image)
            rgba = bytearray(raw)
            rgba[0::4], rgba[2::4], rgba[3::4] = raw[2::4], raw[0::4], b"\xff" * (1280 * 800)
            write_png(output / name, rgba, 1280, 800)
            return rgba

        def lcd(rgba):
            points = [(column, row) for row in range(120, 640) for column in range(180, 880) if rgba[(row * 1280 + column) * 4:(row * 1280 + column) * 4 + 3] == b"\xff\xff\xff"]
            if not points:
                return None
            left, right = min(p[0] for p in points), max(p[0] for p in points)
            top, bottom = min(p[1] for p in points), max(p[1] for p in points)
            width, height = right - left + 1, bottom - top + 1
            scale = width // 160
            if not scale or width != 160 * scale or height != 144 * scale:
                return None
            result = bytearray()
            for row in range(144):
                start = ((top + row * scale) * 1280 + left) * 4
                pixels = rgba[start:start + width * 4]
                for channel in range(4):
                    expected = bytes(value for value in pixels[channel::4][::scale] for _ in range(scale))
                    assert pixels[channel::4] == expected, "LCD is not an integer pixel replica"
                for subrow in range(1, scale):
                    assert rgba[start + subrow * 1280 * 4:start + subrow * 1280 * 4 + width * 4] == pixels
                result.extend(b"".join(pixels[index:index + 4] for index in range(0, width * 4, scale * 4)))
            return bytes(result)

        def key(name, hold=.06):
            x.XSetInputFocus(connection, window, 2, 0)
            code = x.XKeysymToKeycode(connection, x.XStringToKeysym(name.encode()))
            assert code
            xt.XTestFakeKeyEvent(connection, code, 1, 0)
            x.XFlush(connection)
            time.sleep(hold)
            xt.XTestFakeKeyEvent(connection, code, 0, 0)
            x.XFlush(connection)
            time.sleep(.2)

        deadline = time.monotonic() + 20
        while lcd(capture("title.png")) != oracle:
            assert time.monotonic() < deadline, "LCD did not match the native engine oracle"
            time.sleep(.1)
        key("Return", 1.2)
        deadline = time.monotonic() + 10
        while lcd(capture("playing.png")) in (None, oracle):
            assert time.monotonic() < deadline, "Start did not change the game screen"
            time.sleep(.1)
        key("p")
        paused = capture("paused.png")
        time.sleep(.25)
        assert capture("paused-again.png") == paused, "pause did not freeze the window"
        state = Path(str(rom) + ".state")
        assert not state.exists()
        key("F5")
        deadline = time.monotonic() + 5
        while not state.exists():
            assert time.monotonic() < deadline, "F5 did not create a save state"
            time.sleep(.05)
        assert state.stat().st_size > 10000
        capture("saved.png")
        children = Path(f"/proc/{app.pid}/task/{app.pid}/children").read_text().split()
        event = Event()
        event.client = ClientMessage(33, 0, 1, connection, window, x.XInternAtom(connection, b"WM_PROTOCOLS", 0), 32, (c.c_long * 5)(x.XInternAtom(connection, b"WM_DELETE_WINDOW", 0), 0, 0, 0, 0))
        assert x.XSendEvent(connection, window, 0, 0, c.byref(event))
        x.XFlush(connection)
        assert app.wait(timeout=10) == 0, "desktop close failed"
        assert all(not Path("/proc", pid).exists() for pid in children), "engine child survived close"
        window = spawn_window([], "empty-desktop.log")
        time.sleep(.5)
        empty = capture("empty.png")
        for name in ["p", "r", "F5", "F9", "Return", "x", "z", "Left", "Right", "Up", "Down", "Shift_L", "Shift_R", "Tab"]:
            key(name)
            assert capture("empty-after-keys.png") == empty, f"{name} changed the empty desktop"
        event.client.window = window
        assert x.XSendEvent(connection, window, 0, 0, c.byref(event))
        x.XFlush(connection)
        assert app.wait(timeout=10) == 0, "empty desktop close failed"
        print("PASS packaged GPUI: exact LCD, integer scaling, Start, pause, save, clean child exit, inactive empty shortcuts")
    finally:
        if app is not None and app.poll() is None:
            app.terminate()
            try:
                app.wait(timeout=5)
            except subprocess.TimeoutExpired:
                app.kill()
                app.wait()
        if connection:
            x.XCloseDisplay(connection)
        if server is not None:
            server.terminate()
            server.wait(timeout=5)


if __name__ == "__main__":
    main()
