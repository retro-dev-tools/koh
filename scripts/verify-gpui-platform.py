import argparse
import ctypes as c
import ctypes.util
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

from PIL import Image, ImageChops, ImageGrab


def find_lcd(screen, oracle):
    mask = ImageChops.difference(screen, Image.new("RGB", screen.size, "white"))
    mask = mask.convert("L").point(lambda value: 255 if value == 0 else 0)
    raw, width = mask.tobytes(), screen.width
    spans = {}
    for row in range(screen.height):
        for match in re.finditer(b"\xff{160,}", raw[row * width:(row + 1) * width]):
            length = match.end() - match.start()
            if length % 160 == 0:
                spans.setdefault((match.start(), match.end()), []).append(row)
    for (left, right), rows in spans.items():
        scale = (right - left) // 160
        box = left, min(rows), right, max(rows) + 1
        if box[3] - box[1] != 144 * scale:
            continue
        if screen.crop(box).tobytes() == oracle.resize((160 * scale, 144 * scale), Image.Resampling.NEAREST).tobytes():
            return box
    return None


def windows_controls(pid):
    from ctypes import wintypes

    class MonitorInfo(c.Structure):
        _fields_ = [("size", wintypes.DWORD), ("monitor", wintypes.RECT), ("work", wintypes.RECT), ("flags", wintypes.DWORD)]

    user = c.WinDLL("user32", use_last_error=True)
    dwm = c.WinDLL("dwmapi", use_last_error=True)
    user.FindWindowW.argtypes = [c.c_wchar_p, c.c_wchar_p]
    user.FindWindowW.restype = c.c_void_p
    user.GetWindowThreadProcessId.argtypes = [c.c_void_p, c.POINTER(c.c_ulong)]
    user.GetForegroundWindow.argtypes = []
    user.GetForegroundWindow.restype = c.c_void_p
    user.PostMessageW.argtypes = [c.c_void_p, c.c_uint, c.c_size_t, c.c_ssize_t]
    user.MapVirtualKeyW.argtypes = [c.c_uint, c.c_uint]
    user.MonitorFromWindow.argtypes = [c.c_void_p, c.c_uint]
    user.MonitorFromWindow.restype = c.c_void_p
    user.GetMonitorInfoW.argtypes = [c.c_void_p, c.POINTER(MonitorInfo)]
    dwm.DwmGetWindowAttribute.argtypes = [c.c_void_p, c.c_uint, c.c_void_p, c.c_uint]
    dwm.DwmGetWindowAttribute.restype = c.c_long
    window = user.FindWindowW(None, "Koh Emulator")
    owner = c.c_ulong()
    user.GetWindowThreadProcessId(window, c.byref(owner))
    assert window and owner.value == pid, "owned native window not found"
    assert user.GetForegroundWindow() == window, "new desktop is not the foreground window"
    monitor, frame = MonitorInfo(), wintypes.RECT()
    monitor.size = c.sizeof(monitor)
    assert user.GetMonitorInfoW(user.MonitorFromWindow(window, 2), c.byref(monitor))
    assert dwm.DwmGetWindowAttribute(window, 9, c.byref(frame), c.sizeof(frame)) == 0
    rect = lambda value: (value.left, value.top, value.right, value.bottom)
    assert frame.left >= monitor.work.left and frame.top >= monitor.work.top and frame.right <= monitor.work.right and frame.bottom <= monitor.work.bottom, f"native frame outside monitor work area: {rect(frame)} vs {rect(monitor.work)}"

    def key(code, hold=.15):
        scan = user.MapVirtualKeyW(code, 0)
        assert user.PostMessageW(window, 0x100, code, 1 | (scan << 16))
        time.sleep(hold)
        assert user.PostMessageW(window, 0x101, code, 1 | (scan << 16) | (3 << 30))

    def close():
        assert user.PostMessageW(window, 0x10, 0, 0)

    return key, close, [0x0D, 0x50, 0x74]


def macos_controls(pid):
    app = c.CDLL(c.util.find_library("ApplicationServices"))
    core = c.CDLL(c.util.find_library("CoreFoundation"))
    app.CGPreflightPostEventAccess.restype = c.c_bool
    assert app.CGPreflightPostEventAccess(), "runner lacks native event permission"
    app.CGEventCreateKeyboardEvent.argtypes = [c.c_void_p, c.c_ushort, c.c_bool]
    app.CGEventCreateKeyboardEvent.restype = c.c_void_p
    app.CGEventPost.argtypes = [c.c_uint, c.c_void_p]
    core.CFRelease.argtypes = [c.c_void_p]
    core.CFStringCreateWithCString.argtypes = [c.c_void_p, c.c_char_p, c.c_uint]
    core.CFStringCreateWithCString.restype = c.c_void_p
    core.CFArrayGetCount.argtypes = [c.c_void_p]
    core.CFArrayGetCount.restype = c.c_long
    core.CFArrayGetValueAtIndex.argtypes = [c.c_void_p, c.c_long]
    core.CFArrayGetValueAtIndex.restype = c.c_void_p
    core.CFBooleanGetValue.argtypes = [c.c_void_p]
    core.CFBooleanGetValue.restype = c.c_bool
    app.AXUIElementCreateApplication.argtypes = [c.c_int]
    app.AXUIElementCreateApplication.restype = c.c_void_p
    app.AXUIElementCopyAttributeValue.argtypes = [c.c_void_p, c.c_void_p, c.POINTER(c.c_void_p)]
    app.AXUIElementPerformAction.argtypes = [c.c_void_p, c.c_void_p]
    application = app.AXUIElementCreateApplication(pid)
    attribute = core.CFStringCreateWithCString(None, b"AXFrontmost", 0x08000100)
    frontmost = c.c_void_p()
    try:
        assert application and attribute
        assert app.AXUIElementCopyAttributeValue(application, attribute, c.byref(frontmost)) == 0
        assert core.CFBooleanGetValue(frontmost), "new desktop is not the foreground application"
    finally:
        if frontmost.value:
            core.CFRelease(frontmost)
        if attribute:
            core.CFRelease(attribute)
        if application:
            core.CFRelease(application)

    def key(code, hold=.15):
        for down in [True, False]:
            event = app.CGEventCreateKeyboardEvent(None, code, down)
            assert event
            app.CGEventPost(0, event)
            core.CFRelease(event)
            if down:
                time.sleep(hold)

    def close():
        resources = []
        try:
            def name(value):
                result = core.CFStringCreateWithCString(None, value.encode(), 0x08000100)
                assert result
                resources.append(result)
                return result

            application = app.AXUIElementCreateApplication(pid)
            assert application
            resources.append(application)
            windows = c.c_void_p()
            assert app.AXUIElementCopyAttributeValue(application, name("AXWindows"), c.byref(windows)) == 0
            resources.append(windows.value)
            assert core.CFArrayGetCount(windows) == 1
            window = core.CFArrayGetValueAtIndex(windows, 0)
            button = c.c_void_p()
            assert app.AXUIElementCopyAttributeValue(window, name("AXCloseButton"), c.byref(button)) == 0
            resources.append(button.value)
            assert app.AXUIElementPerformAction(button, name("AXPress")) == 0
        finally:
            for resource in reversed(resources):
                core.CFRelease(resource)

    return key, close, [36, 35, 96]


def main():
    parser = argparse.ArgumentParser(description="Real Windows/macOS GPUI window acceptance")
    parser.add_argument("desktop", type=Path)
    parser.add_argument("rom", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    assert sys.platform in ("win32", "darwin")
    if sys.platform == "win32":
        user = c.WinDLL("user32", use_last_error=True)
        user.SetProcessDpiAwarenessContext.argtypes = [c.c_void_p]
        user.SetProcessDpiAwarenessContext(c.c_void_p(-4))
    desktop, output = args.desktop.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    rom = output / "2048.gb"
    shutil.copyfile(args.rom, rom)
    suffix = ".exe" if sys.platform == "win32" else ""
    engine = desktop.with_name("Koh.Emulator.App" + suffix)
    assert engine.is_file()
    environment = dict(os.environ, ALSOFT_DRIVERS="null")
    environment.pop("KOH_ENGINE", None)
    with (output / "oracle.log").open("w") as log:
        subprocess.run([str(engine), str(rom), "--headless", "--frames=480", "--screenshot=" + str(output / "oracle.png")], env=environment, stdout=log, stderr=log, check=True, timeout=30)
    oracle = Image.open(output / "oracle.png").convert("RGB")
    assert oracle.size == (160, 144)
    with (output / "desktop.log").open("w") as log:
        process = subprocess.Popen([str(desktop), str(rom)], env=environment, stdout=log, stderr=log)
    try:
        def capture(name):
            assert process.poll() is None, "desktop exited before native acceptance"
            screen = ImageGrab.grab().convert("RGB")
            screen.save(output / name)
            return screen

        deadline, box = time.monotonic() + 45, None
        while box is None:
            box = find_lcd(capture("title.png"), oracle)
            assert time.monotonic() < deadline, "native LCD did not match the engine oracle"
            time.sleep(.1)
        print(f"PASS {sys.platform}: exact LCD oracle and {(box[2] - box[0]) // 160}x integer replication", flush=True)
        key, close, codes = (windows_controls if sys.platform == "win32" else macos_controls)(process.pid)
        key(codes[0], 1.2)
        deadline = time.monotonic() + 10
        while capture("playing.png").crop(box).tobytes() == oracle.resize((box[2] - box[0], box[3] - box[1]), Image.Resampling.NEAREST).tobytes():
            assert time.monotonic() < deadline, "Start did not change the game screen"
            time.sleep(.1)
        key(codes[1])
        previous, deadline = capture("paused.png"), time.monotonic() + 10
        while True:
            time.sleep(.5)
            following = capture("paused-again.png")
            if following.crop(box).tobytes() == previous.crop(box).tobytes():
                break
            assert time.monotonic() < deadline, "pause did not freeze the game"
            previous = following
        key(codes[2])
        state, deadline = Path(str(rom) + ".state"), time.monotonic() + 10
        while not state.exists():
            assert time.monotonic() < deadline, "F5 did not save state"
            time.sleep(.05)
        assert state.stat().st_size > 10000
        saved, modified = state.read_bytes(), state.stat().st_mtime_ns
        time.sleep(.5)
        key(codes[2])
        deadline = time.monotonic() + 10
        while state.stat().st_mtime_ns == modified:
            assert time.monotonic() < deadline, "second F5 did not complete"
            time.sleep(.05)
        assert state.read_bytes() == saved, "paused machine state advanced"
        close()
        assert process.wait(timeout=15) == 0, "native window close failed"
        print(f"PASS {sys.platform}: Start, pause, save and native close", flush=True)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
