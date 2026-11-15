# SpireRemote host - dedicated remote host for Slay the Spire 2 (Steam AppID 2868840)
# Runs in the interactive desktop session on the user's PC.
# Serves a phone PWA + MJPEG stream of ONLY the game window, and injects
# mouse/keyboard input ONLY while the game window is in the foreground.
# Binds 127.0.0.1 only; reached from the phone via Tailscale Serve (tailnet HTTPS).
import ctypes
import glob
import io
import json
import os
import queue
import socket
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from ctypes import wintypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

from PIL import Image, ImageDraw, ImageGrab

# The PC display runs at 200% scaling. If this process stays DPI-unaware,
# Windows virtualizes its coordinates: captures and SendInput positions land
# in a scaled space that does not match the game's real pixels, so clicks
# miss. Claim per-monitor awareness before touching any window API.
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass

BASE = os.path.dirname(os.path.abspath(__file__))
WWW = os.path.join(BASE, "www")
LOG_PATH = os.path.join(BASE, "host.log")
CONFIG_PATH = os.path.join(BASE, "config.json")

STEAM_EXE = r"C:\Program Files (x86)\Steam\steam.exe"
APPID = "2868840"
GAME_PROC = "SlayTheSpire2.exe"
GAME_TITLE_HINT = "Slay the Spire 2"
PORT = 8765
MAX_W = 1152          # stream width cap (px)
DESIRED_W = 1152      # most recently requested stream width
JPEG_Q = 62
JPEG_MAX_BYTES = 45 * 1024   # per-frame size budget; quality steps down to fit
JPEG_Q_STEPS = (64, 56, 48, 40, 32, 24)
FPS = 15              # capture fps while game is running
INPUT_WAKE = threading.Event()
LAST_INPUT = {"t": 0.0}
LAT_LOCK = threading.Lock()
LAT = {"post": [], "frame": [], "pending": None}

def latency_status():
    with LAT_LOCK:
        posts, frames = LAT["post"], LAT["frame"]
        return {
            "post_ms": posts[-1] if posts else None,
            "frame_after_ms": frames[-1] if frames else None,
            "post_med_ms": statistics.median(posts) if posts else None,
            "frame_med_ms": statistics.median(frames) if frames else None,
            "n": len(posts),
        }

def publish_frame(jpeg, rect):
    # Serialize publication with input completion so the first later frame wins.
    with LAT_LOCK:
        FRAMES.set(jpeg, rect)
        if LAT["pending"] is not None:
            LAT["frame"].append((time.perf_counter() - LAT["pending"]) * 1000)
            del LAT["frame"][:-20]
            LAT["pending"] = None

def encode_jpeg(img):
    """Encode to JPEG, stepping quality down until the frame fits the budget."""
    data = None
    for q in JPEG_Q_STEPS:
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=q)
        data = buf.getvalue()
        if len(data) <= JPEG_MAX_BYTES:
            break
    return data

def log(msg):
    line = "%s %s" % (time.strftime("%Y-%m-%d %H:%M:%S"), msg)
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass
    print(line, flush=True)

def load_token(create=False):
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8-sig") as f:
            return json.load(f)["token"]
    except Exception:
        pass
    if not create:
        return ""
    # First run: create a config with a fresh random token.
    try:
        import secrets
        token = secrets.token_hex(20)
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump({"token": token}, f)
        log("created config.json with a fresh token")
        return token
    except Exception as e:
        log("config create failed: %r" % e)
        return ""

TOKEN = load_token()

# ---------------- Win32 ----------------
user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

def _client_rect(hwnd):
    """Client area of hwnd in screen coordinates (excludes title bar/borders)."""
    cr = wintypes.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(cr)):
        return None
    pt = wintypes.POINT(0, 0)
    if not user32.ClientToScreen(hwnd, ctypes.byref(pt)):
        return None
    return (pt.x, pt.y, pt.x + cr.right, pt.y + cr.bottom)

def find_game_window():
    """Return (hwnd, client_rect) for the largest visible window titled like the game."""
    found = []
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        title = buf.value
        if GAME_TITLE_HINT in title:
            r = wintypes.RECT()
            if user32.GetWindowRect(hwnd, ctypes.byref(r)):
                area = max(0, r.right - r.left) * max(0, r.bottom - r.top)
                found.append((area, hwnd))
        return True
    user32.EnumWindows(EnumWindowsProc(cb), 0)
    if not found:
        return None, None
    found.sort(reverse=True)
    hwnd = found[0][1]
    rect = _client_rect(hwnd)
    if not rect:
        r = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        rect = (r.left, r.top, r.right, r.bottom)
    return hwnd, rect

def game_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq " + GAME_PROC, "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=5,
        ).stdout
        return GAME_PROC.lower() in out.lower()
    except Exception:
        return False

def steam_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq steam.exe", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=5,
        ).stdout
        return "steam.exe" in out.lower()
    except Exception:
        return False

def focus_window(hwnd):
    if not hwnd:
        return False
    try:
        if user32.IsIconic(hwnd):
            user32.ShowWindow(hwnd, 9)  # SW_RESTORE
        fg = user32.GetForegroundWindow()
        if fg == hwnd:
            return True
        cur_tid = kernel32.GetCurrentThreadId()
        fg_tid = user32.GetWindowThreadProcessId(fg, None) if fg else 0
        tgt_tid = user32.GetWindowThreadProcessId(hwnd, None)
        attached = False
        if fg_tid and fg_tid != cur_tid:
            attached = bool(user32.AttachThreadInput(cur_tid, fg_tid, True))
        user32.BringWindowToTop(hwnd)
        ok = bool(user32.SetForegroundWindow(hwnd))
        if attached:
            user32.AttachThreadInput(cur_tid, fg_tid, False)
        if not ok:
            ok = user32.GetForegroundWindow() == hwnd
        return ok or user32.GetForegroundWindow() == hwnd
    except Exception as e:
        log("focus failed: %r" % e)
        return False

# ---- SendInput ----
INPUT_MOUSE, INPUT_KEYBOARD = 0, 1
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_WHEEL = 0x0800
MOUSEEVENTF_VIRTUALDESK = 0x4000
MOUSEEVENTF_ABSOLUTE = 0x8000
KEYEVENTF_KEYUP = 0x0002
SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN = 76, 77
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79

class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_void_p)]

class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_void_p)]

class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]

class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]

def _send(inputs):
    arr = (INPUT * len(inputs))(*inputs)
    user32.SendInput(len(inputs), arr, ctypes.sizeof(INPUT))

def _mouse(flags, nx=0, ny=0, data=0):
    i = INPUT()
    i.type = INPUT_MOUSE
    i.mi = MOUSEINPUT(nx, ny, data, flags, 0, None)
    return i

def _to_abs(px, py):
    vx = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
    vy = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    vw = max(1, user32.GetSystemMetrics(SM_CXVIRTUALSCREEN))
    vh = max(1, user32.GetSystemMetrics(SM_CYVIRTUALSCREEN))
    nx = int((px - vx) * 65535 / vw)
    ny = int((py - vy) * 65535 / vh)
    return max(0, min(65535, nx)), max(0, min(65535, ny))

ABSMOVE = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK

def mouse_move_to(px, py):
    nx, ny = _to_abs(px, py)
    _send([_mouse(ABSMOVE, nx, ny)])

def mouse_button(px, py, down=True, right=False):
    nx, ny = _to_abs(px, py)
    if right:
        flag = MOUSEEVENTF_RIGHTDOWN if down else MOUSEEVENTF_RIGHTUP
    else:
        flag = MOUSEEVENTF_LEFTDOWN if down else MOUSEEVENTF_LEFTUP
    _send([_mouse(ABSMOVE, nx, ny), _mouse(flag, nx, ny)])

def mouse_click(px, py, right=False):
    mouse_button(px, py, True, right)
    time.sleep(0.025)
    mouse_button(px, py, False, right)

VK = {"esc": 0x1B, "escape": 0x1B, "space": 0x20, "e": 0x45, "m": 0x4D,
      "tab": 0x09, "enter": 0x0D, "q": 0x51, "w": 0x57, "up": 0x26,
      "down": 0x28, "left": 0x25, "right": 0x27}
for _d in range(1, 10):
    VK[str(_d)] = 0x30 + _d
VK["0"] = 0x30

def press_key(name):
    vk = VK.get(str(name).lower())
    if not vk:
        return False
    d = INPUT(); d.type = INPUT_KEYBOARD; d.ki = KEYBDINPUT(vk, 0, 0, 0, None)
    u = INPUT(); u.type = INPUT_KEYBOARD; u.ki = KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None)
    _send([d]); time.sleep(0.018); _send([u])
    return True

# ---------------- Capture ----------------
class FrameStore:
    def __init__(self):
        self.cond = threading.Condition()
        self.jpeg = None
        self.counter = 0
        self.ts = 0.0
        self.rect = None
        self.thumb_sig = None

    def set(self, jpeg, rect):
        with self.cond:
            self.jpeg = jpeg
            self.counter += 1
            self.ts = time.time()
            self.rect = rect
            self.cond.notify_all()

    def get(self):
        with self.cond:
            return self.jpeg, self.counter, self.ts, self.rect

    def wait_newer(self, since, timeout):
        """Block until a frame with counter > since exists; None on timeout."""
        end = time.time() + timeout
        with self.cond:
            while self.counter <= since or self.jpeg is None:
                left = end - time.time()
                if left <= 0:
                    return None
                self.cond.wait(left)
            return self.jpeg, self.counter, self.ts, self.rect

FRAMES = FrameStore()

# Last time any PWA client touched the host; drives the focus keeper so the
# game stays in front while someone is playing remotely, and is left alone
# when nobody is watching.
LAST_CLIENT = {"ts": 0.0}

def touch_client():
    LAST_CLIENT["ts"] = time.time()

def placeholder_jpeg(text_lines):
    img = Image.new("RGB", (MAX_W, 619), (11, 14, 20))
    d = ImageDraw.Draw(img)
    y = 60
    for i, line in enumerate(text_lines):
        d.text((40, y), line, fill=(140, 220, 240) if i == 0 else (200, 210, 220))
        y += 34
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=70)
    return buf.getvalue()

# ---- Windows Graphics Capture (window-only; ignores taskbar/popups/other windows) ----
WGC = {"img": None, "ts": 0.0, "hwnd": None, "cap": None, "control": None, "failed_until": 0.0}

def _wgc_run(cap):
    try:
        cap.start()
    except Exception as e:
        log("wgc run error: %r" % e)
    WGC["cap"] = None
    WGC["control"] = None

def _wgc_start(hwnd):
    try:
        from windows_capture import WindowsCapture
    except Exception as e:
        log("windows_capture unavailable: %r" % e)
        WGC["failed_until"] = time.time() + 60
        return
    try:
        if WGC["control"] is not None:
            try:
                WGC["control"].stop()
            except Exception:
                pass
            WGC["control"] = None
        cap = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=int(hwnd))

        @cap.event
        def on_frame_arrived(frame, capture_control):
            WGC["control"] = capture_control
            try:
                img = None
                try:
                    import numpy as np
                    a = np.asarray(frame.frame_buffer)
                    if a.ndim == 3 and a.shape[2] == 4:
                        if a.shape[1] != frame.width:
                            a = a[:, :frame.width, :]
                        img = Image.fromarray(np.ascontiguousarray(a), "RGBA").convert("RGB")
                except Exception:
                    img = None
                if img is None:
                    img = Image.frombuffer("RGBA", (frame.width, frame.height),
                                           bytes(frame.frame_buffer), "raw", "BGRA", 0, 1).convert("RGB")
                WGC["img"] = img
                WGC["ts"] = time.time()
            except Exception as e:
                log("wgc frame error: %r" % e)

        @cap.event
        def on_closed():
            WGC["cap"] = None
            WGC["control"] = None

        WGC["cap"] = cap
        WGC["hwnd"] = hwnd
        threading.Thread(target=_wgc_run, args=(cap,), daemon=True).start()
        log("wgc started for hwnd=%s" % hwnd)
    except Exception as e:
        log("wgc start error: %r" % e)
        WGC["failed_until"] = time.time() + 30

def _wgc_client_image(rect):
    """Latest WGC full-window image cropped to the client area, or None."""
    img = WGC["img"]
    if img is None or (time.time() - WGC["ts"]) > 3.0:
        return None
    hwnd = WGC["hwnd"]
    if not hwnd:
        return None
    wr = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(wr)):
        return None
    ox = rect[0] - wr.left
    oy = rect[1] - wr.top
    cw = rect[2] - rect[0]
    ch = rect[3] - rect[1]
    x0 = max(0, min(ox, img.width - 1))
    y0 = max(0, min(oy, img.height - 1))
    x1 = max(x0 + 1, min(ox + cw, img.width))
    y1 = max(y0 + 1, min(oy + ch, img.height))
    return img.crop((x0, y0, x1, y1))

def capture_loop():
    last_sig_check = 0.0
    last_focus_check = 0.0
    force_encode = False
    while True:
        with LAT_LOCK:
            force_encode = force_encode or INPUT_WAKE.is_set()
            input_seen = LAST_INPUT["t"]
        try:
            hwnd, rect = find_game_window()
            if hwnd and (time.time() - last_focus_check) > 2.0:
                last_focus_check = time.time()
                if (time.time() - LAST_CLIENT["ts"]) < 10 and user32.GetForegroundWindow() != hwnd:
                    focus_window(hwnd)
            if hwnd and rect:
                l, t, r, b = rect
                w, h = r - l, b - t
                if w > 50 and h > 50:
                    img = None
                    if time.time() > WGC["failed_until"]:
                        if WGC["cap"] is None or WGC["hwnd"] != hwnd:
                            _wgc_start(hwnd)
                        img = _wgc_client_image(rect)
                    if img is None:
                        img = ImageGrab.grab(bbox=(l, t, r, b), all_screens=True)
                    target_w = min(img.width, DESIRED_W, MAX_W)
                    if img.width > target_w:
                        img = img.resize((target_w, max(1, int(img.height * target_w / img.width))), Image.BILINEAR)
                    # cheap change detection on a thumbnail
                    now = time.time()
                    sig = None
                    if now - last_sig_check > 0.1:
                        thumb = img.resize((32, 18)).convert("L")
                        sig = hash(thumb.tobytes())
                        last_sig_check = now
                    prev_jpeg, prev_counter, prev_ts, _ = FRAMES.get()
                    if not force_encode and sig is not None and sig == FRAMES.thumb_sig and (now - prev_ts) < 1.5:
                        continue
                    if sig is not None:
                        FRAMES.thumb_sig = sig
                    publish_frame(encode_jpeg(img), rect)
            else:
                if WGC["control"] is not None:
                    try:
                        WGC["control"].stop()
                    except Exception:
                        pass
                    WGC["control"] = None
                    WGC["cap"] = None
                    WGC["img"] = None
                prev_jpeg, _, prev_ts, _ = FRAMES.get()
                if force_encode or prev_jpeg is None or (time.time() - prev_ts) > 2.0 or FRAMES.rect is not None:
                    publish_frame(placeholder_jpeg([
                        "Spire Remote - Slay the Spire 2",
                        "Game is not running.",
                        "Tap LAUNCH in the PWA to start it from Steam.",
                        time.strftime("%H:%M:%S"),
                    ]), None)
        except Exception as e:
            log("capture error: %r" % e)
        finally:
            with LAT_LOCK:
                # Do not clear a newer input that arrived during this capture.
                if LAST_INPUT["t"] == input_seen:
                    INPUT_WAKE.clear()
            INPUT_WAKE.wait(1.0 / FPS)
            force_encode = INPUT_WAKE.is_set()

# ---------------- Audio (WASAPI loopback -> ffmpeg MP3) ----------------
# Captures the PC's default render device via PyAudioWPatch loopback and
# transcodes to MP3 with ffmpeg (encoder only; the essentials build has no
# wasapi demuxer). Capture runs only while at least one client is connected
# (plus a 30s grace period after the last one leaves).
def find_ffmpeg():
    pats = glob.glob(r"C:\Users\nekom\scoop\apps\ffmpeg-essentials\*\bin\ffmpeg.exe")
    if pats:
        pats.sort()
        return pats[-1]
    import shutil
    return shutil.which("ffmpeg")

class AudioHub:
    def __init__(self):
        self.lock = threading.Lock()
        self.clients = {}
        self.next_id = 0
        self.running = False
        self.stop_flag = False
        self.last_gone = 0.0
        self._pa = None
        self._stream = None
        self._ffmpeg = None

    def subscribe(self):
        with self.lock:
            self.next_id += 1
            cid = self.next_id
            q = queue.Queue(maxsize=96)
            self.clients[cid] = q
            need_start = not self.running
            if need_start:
                self.running = True
                self.stop_flag = False
        if need_start:
            threading.Thread(target=self._run, daemon=True).start()
            threading.Thread(target=self._reaper, daemon=True).start()
        return cid, q

    def unsubscribe(self, cid):
        with self.lock:
            self.clients.pop(cid, None)
            if not self.clients:
                self.last_gone = time.time()

    def client_count(self):
        with self.lock:
            return len(self.clients)

    def _broadcast(self, ff):
        try:
            while not self.stop_flag and ff.stdout:
                chunk = ff.stdout.read(8192)
                if not chunk:
                    break
                with self.lock:
                    targets = list(self.clients.values())
                for q in targets:
                    try:
                        q.put_nowait(chunk)
                    except queue.Full:
                        try:
                            q.get_nowait()
                        except queue.Empty:
                            pass
                        try:
                            q.put_nowait(chunk)
                        except queue.Full:
                            pass
        except Exception as e:
            log("audio broadcast error: %r" % e)

    def _run(self):
        pa = None
        try:
            ff_path = find_ffmpeg()
            if not ff_path:
                log("audio: ffmpeg not found")
                return
            import pyaudiowpatch as pyaudio
            pa = pyaudio.PyAudio()
            self._pa = pa
            dev = pa.get_default_wasapi_loopback()
            rate = int(dev["defaultSampleRate"])
            ch = max(1, min(2, int(dev["maxInputChannels"])))
            stream = pa.open(format=pyaudio.paInt16, channels=ch, rate=rate,
                             input=True, input_device_index=dev["index"],
                             frames_per_buffer=2048)
            self._stream = stream
            ff = subprocess.Popen(
                [ff_path, "-hide_banner", "-loglevel", "error",
                 "-f", "s16le", "-ar", str(rate), "-ac", str(ch), "-i", "-",
                 "-codec:a", "libmp3lame", "-b:a", "64k",
                 "-ar", "44100", "-ac", "1", "-f", "mp3", "-"],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL)
            self._ffmpeg = ff
            threading.Thread(target=self._broadcast, args=(ff,), daemon=True).start()
            log("audio started: loopback @%dHz ch=%d -> mp3 64k mono" % (rate, ch))
            while not self.stop_flag:
                data = stream.read(2048, exception_on_overflow=False)
                try:
                    ff.stdin.write(data)
                except (BrokenPipeError, OSError):
                    break
        except Exception as e:
            log("audio run error: %r" % e)
        finally:
            self._teardown()
            with self.lock:
                self.running = False

    def _teardown(self):
        # Runs ONLY in the _run thread (from its finally). Never touch the
        # PortAudio stream from any other thread: closing it while _run is
        # blocked in stream.read() crashed the whole host with 0xC0000005.
        try:
            if self._ffmpeg is not None:
                if self._ffmpeg.stdin:
                    self._ffmpeg.stdin.close()
                try:
                    self._ffmpeg.wait(timeout=3)
                except Exception:
                    self._ffmpeg.terminate()
        except Exception:
            pass
        self._ffmpeg = None
        try:
            if self._stream is not None:
                self._stream.stop_stream()
                self._stream.close()
        except Exception:
            pass
        self._stream = None
        try:
            if self._pa is not None:
                self._pa.terminate()
        except Exception:
            pass
        self._pa = None

    def _reaper(self):
        while True:
            time.sleep(5)
            with self.lock:
                if not self.running:
                    return
                idle = (not self.clients) and self.last_gone and (time.time() - self.last_gone > 30)
            if idle:
                log("audio idle 30s; stopping capture")
                # Only raise the flag: _run notices within one read cycle
                # (~43ms) and tears everything down in its own thread.
                self.stop_flag = True
                return

AUDIO = AudioHub()

# ---------------- Actions ----------------
state_lock = threading.Lock()
launching = {"active": False}

def do_launch():
    with state_lock:
        if launching["active"]:
            return {"ok": True, "state": "launching"}
        if game_running():
            hwnd, _ = find_game_window()
            if hwnd:
                focus_window(hwnd)
            return {"ok": True, "state": "already_running"}
        launching["active"] = True

    def worker():
        try:
            if not steam_running():
                subprocess.Popen([STEAM_EXE])
                for _ in range(60):
                    if steam_running():
                        break
                    time.sleep(1)
                time.sleep(5)
            subprocess.Popen([STEAM_EXE, "steam://rungameid/" + APPID])
            log("launch fired: steam://rungameid/" + APPID)
            for _ in range(120):
                hwnd, _ = find_game_window()
                if hwnd:
                    time.sleep(2)
                    focus_window(hwnd)
                    log("game window found and focused")
                    break
                time.sleep(1)
        except Exception as e:
            log("launch error: %r" % e)
        finally:
            with state_lock:
                launching["active"] = False

    threading.Thread(target=worker, daemon=True).start()
    return {"ok": True, "state": "launching"}

def do_quit(force=False):
    hwnd, _ = find_game_window()
    if not hwnd and not game_running():
        return {"ok": True, "state": "not_running"}
    if force:
        subprocess.run(["taskkill", "/IM", GAME_PROC, "/F"], capture_output=True, timeout=10)
        log("force quit sent")
        return {"ok": True, "state": "force_quit"}
    if hwnd:
        user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
        log("WM_CLOSE sent to game window")
        return {"ok": True, "state": "close_sent"}
    return {"ok": False, "state": "no_window", "error": "process running but no window yet"}

def status():
    hwnd, rect = find_game_window()
    _, _, fts, _ = FRAMES.get()
    return {
        "ok": True,
        "steam": steam_running(),
        "game": bool(hwnd) or game_running(),
        "launching": launching["active"],
        "focused": bool(hwnd) and user32.GetForegroundWindow() == hwnd,
        "rect": rect,
        "frame_age_ms": int((time.time() - fts) * 1000) if fts else None,
        "audio_clients": AUDIO.client_count(),
        "lat": latency_status(),
        "version": "v0.113.1 / build 26625880",
    }

def handle_input(body):
    with LAT_LOCK:
        LAST_INPUT["t"] = time.time()
        INPUT_WAKE.set()
    hwnd, rect = find_game_window()
    if not hwnd or not rect:
        return {"ok": False, "error": "game window not found"}
    if not focus_window(hwnd):
        return {"ok": False, "error": "could not focus game window"}
    typ = body.get("type")
    l, t, r, b = rect
    w, h = r - l, b - t

    def to_px(x, y):
        return int(l + float(x) * w), int(t + float(y) * h)

    if typ in ("tap", "rtap", "down", "up", "move", "hover"):
        if "x" not in body or "y" not in body:
            return {"ok": False, "error": "x/y required"}
        px, py = to_px(body["x"], body["y"])
        if typ == "tap":
            mouse_click(px, py, right=False)
        elif typ == "rtap":
            mouse_click(px, py, right=True)
        elif typ == "down":
            mouse_button(px, py, True, right=bool(body.get("right")))
        elif typ == "up":
            mouse_button(px, py, False, right=bool(body.get("right")))
        elif typ in ("move", "hover"):
            mouse_move_to(px, py)
        return {"ok": True}
    if typ == "key":
        if press_key(body.get("key", "")):
            return {"ok": True}
        return {"ok": False, "error": "unknown key"}
    return {"ok": False, "error": "unknown input type"}

# ---------------- HTTP ----------------
def _bridge(method, path, body_obj=None, timeout=6):
    """Relay only the fixed loopback bridge routes; never synthesize game state."""
    data = json.dumps(body_obj).encode("utf-8") if body_obj is not None else None
    req = urllib.request.Request("http://127.0.0.1:8766" + path, data=data,
                                 headers={"Content-Type": "application/json"}, method=method)
    try:
        try:
            response = urllib.request.urlopen(req, timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            code = response.code
            raw = response.read()
        try:
            return json.loads(raw), code
        except (ValueError, UnicodeError):
            return {"ok": False, "error": "bridge_bad_response"}, 502
    except (urllib.error.URLError, TimeoutError, OSError):
        return {"ok": False, "error": "bridge_unreachable"}, 502

STATIC = {
    "/": ("index.html", "text/html; charset=utf-8"),
    "/index.html": ("index.html", "text/html; charset=utf-8"),
    "/manifest.webmanifest": ("manifest.webmanifest", "application/manifest+json"),
    "/sw.js": ("sw.js", "text/javascript"),
    "/icon-192.png": ("icon-192.png", "image/png"),
    "/icon-512.png": ("icon-512.png", "image/png"),
    "/icon-180.png": ("icon-180.png", "image/png"),
}

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def log_message(self, fmt, *args):
        pass

    def _norm(self, raw_path):
        p = urlparse(raw_path).path
        if p.startswith("/spire/"):
            p = p[len("/spire"):]
        elif p == "/spire":
            p = "/"
        return p

    def _authed(self):
        q = parse_qs(urlparse(self.path).query)
        tok = self.headers.get("X-Token") or (q.get("t") or [None])[0]
        if not tok:
            cookie = self.headers.get("Cookie") or ""
            for part in cookie.split(";"):
                part = part.strip()
                if part.startswith("spire_t="):
                    tok = part[len("spire_t="):]
                    break
        return bool(TOKEN) and tok == TOKEN

    def _json(self, obj, code=200):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        global DESIRED_W
        path = self._norm(self.path)
        if path in STATIC:
            fname, ctype = STATIC[path]
            fp = os.path.join(WWW, fname)
            try:
                with open(fp, "rb") as f:
                    data = f.read()
            except Exception:
                return self._json({"ok": False, "error": "missing " + fname}, 404)
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            if fname in ("index.html", "sw.js"):
                self.send_header("Cache-Control", "no-cache")
            if fname == "sw.js":
                self.send_header("Service-Worker-Allowed", "/")
            if fname == "index.html":
                q = parse_qs(urlparse(self.path).query)
                qt = (q.get("t") or [None])[0]
                if TOKEN and qt == TOKEN:
                    self.send_header("Set-Cookie", "spire_t=%s; Path=/; Max-Age=31536000; SameSite=Lax" % TOKEN)
            self.end_headers()
            self.wfile.write(data)
            return
        if path == "/api/status":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            return self._json(status())
        if path in ("/api/state", "/api/actions"):
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            result, code = _bridge("GET", path[len("/api"):], None, 6)
            return self._json(result, code)
        if path == "/api/frame.jpg":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            q = parse_qs(urlparse(self.path).query)
            since = None
            if "w" in q:
                try:
                    DESIRED_W = max(480, min(1152, int(q["w"][0])))
                except (ValueError, TypeError):
                    pass
            if "since" in q:
                try:
                    since = int(q["since"][0])
                except Exception:
                    since = None
            if since is not None:
                # Long-poll: wait up to 1.5s for a frame newer than `since`.
                got = FRAMES.wait_newer(since, 1.5)
                if got is None:
                    self.send_response(204)
                    self.send_header("Content-Length", "0")
                    self.send_header("Cache-Control", "no-store")
                    self.end_headers()
                    return
                jpeg, counter, fts, _ = got
            else:
                jpeg, counter, fts, _ = FRAMES.get()
            if not jpeg:
                return self._json({"ok": False, "error": "no frame yet"}, 503)
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(jpeg)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Frame-Counter", str(counter))
            self.send_header("X-Frame-Age-Ms", str(int((time.time() - fts) * 1000)) if fts else "-1")
            self.end_headers()
            self.wfile.write(jpeg)
            return
        if path == "/api/audio":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            cid, aq = AUDIO.subscribe()
            self.send_response(200)
            self.send_header("Content-Type", "audio/mpeg")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            try:
                while True:
                    try:
                        chunk = aq.get(timeout=20)
                    except queue.Empty:
                        break
                    self.wfile.write(chunk)
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass
            except Exception:
                pass
            finally:
                AUDIO.unsubscribe(cid)
            return
        if path == "/api/frame_pw.jpg":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            try:
                hwnd, rect = find_game_window()
                if not hwnd or not rect:
                    return self._json({"ok": False, "error": "game window not found"}, 404)
                img = ImageGrab.grab(window=hwnd)
                wr = wintypes.RECT()
                user32.GetWindowRect(hwnd, ctypes.byref(wr))
                ox = rect[0] - wr.left
                oy = rect[1] - wr.top
                cw = rect[2] - rect[0]
                ch = rect[3] - rect[1]
                img = img.crop((ox, oy, ox + cw, oy + ch))
                buf = io.BytesIO()
                img.save(buf, "JPEG", quality=88)
                jpeg = buf.getvalue()
                w, h = img.width, img.height
            except Exception as e:
                return self._json({"ok": False, "error": "pw capture failed: %r" % e}, 500)
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(jpeg)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Frame-Size", f"{w}x{h}")
            self.end_headers()
            self.wfile.write(jpeg)
            return
        if path == "/api/frame_full.jpg":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            try:
                hwnd, rect = find_game_window()
                if not hwnd or not rect:
                    return self._json({"ok": False, "error": "game window not found"}, 404)
                l, t, r, b = rect
                img = _wgc_client_image(rect)
                if img is None:
                    img = ImageGrab.grab(bbox=(l, t, r, b), all_screens=True)
                buf = io.BytesIO()
                img.save(buf, "JPEG", quality=88)
                jpeg = buf.getvalue()
                w, h = img.width, img.height
            except Exception as e:
                return self._json({"ok": False, "error": "capture failed: %r" % e}, 500)
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(jpeg)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Frame-Size", f"{w}x{h}")
            self.end_headers()
            self.wfile.write(jpeg)
            return
        if path == "/stream.mjpg":
            if not self._authed():
                return self._json({"ok": False, "error": "unauthorized"}, 401)
            touch_client()
            self.send_response(200)
            self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            last = -1
            try:
                while True:
                    touch_client()
                    jpeg, counter, _, _ = FRAMES.get()
                    if jpeg and counter != last:
                        last = counter
                        self.wfile.write(b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "
                                         + str(len(jpeg)).encode() + b"\r\n\r\n" + jpeg + b"\r\n")
                        self.wfile.flush()
                    time.sleep(0.05)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                return
            except Exception:
                return
            return
        return self._json({"ok": False, "error": "not found"}, 404)

    def do_POST(self):
        received = time.perf_counter()
        path = self._norm(self.path)
        if not path.startswith("/api/"):
            return self._json({"ok": False, "error": "not found"}, 404)
        if not self._authed():
            return self._json({"ok": False, "error": "unauthorized"}, 401)
        touch_client()
        try:
            n = int(self.headers.get("Content-Length") or 0)
            body = json.loads(self.rfile.read(n).decode("utf-8")) if n else {}
        except Exception:
            body = {}
        if path == "/api/launch":
            return self._json(do_launch())
        if path == "/api/quit":
            return self._json(do_quit(force=bool(body.get("force"))))
        if path == "/api/focus":
            hwnd, _ = find_game_window()
            return self._json({"ok": bool(hwnd) and focus_window(hwnd)})
        if path == "/api/input":
            try:
                result = handle_input(body)
            finally:
                with LAT_LOCK:
                    completed = time.perf_counter()
                    LAT["post"].append((completed - received) * 1000)
                    del LAT["post"][:-20]
                    LAT["pending"] = completed
            return self._json(result)
        if path == "/api/do":
            result, code = _bridge("POST", "/do", body, 7)
            return self._json(result, code)
        return self._json({"ok": False, "error": "not found"}, 404)

def main():
    global TOKEN
    if not TOKEN:
        TOKEN = load_token(create=True)
    if not TOKEN:
        log("WARNING: no token configured")
    threading.Thread(target=capture_loop, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    srv.daemon_threads = True
    log("SpireRemote host listening on 127.0.0.1:%d" % PORT)
    srv.serve_forever()

if __name__ == "__main__":
    main()
