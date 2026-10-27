"""Захват видео с прибора и полноэкранный вывод на выбранный экран."""
import os
import sys
import threading
import time

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

WINDOW = "RikaNV"
STANDBY_AFTER = 2.5  # секунд без кадров до заставки
MAX_RENDER_WIDTH = 1920
INK = (30, 41, 20)  # фирменный тёмно-зелёный, BGR


def resource(name):
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, name)


class FrameSource(threading.Thread):
    """Читает кадры в отдельном потоке и хранит только последний: так нет накопления задержки."""

    def __init__(self, source):
        super().__init__(daemon=True)
        self.source = source
        self.lock = threading.Lock()
        self.frame = None
        self.frame_time = 0.0
        self.frame_count = 0
        self.status = "Подключение…"
        self.fps = 0.0
        self.stopped = threading.Event()

    def latest(self):
        with self.lock:
            return self.frame, self.frame_time, self.frame_count

    def stop(self):
        self.stopped.set()

    def _open(self):
        kind, value = self.source["kind"], self.source["value"]
        if kind == "url":
            transport = self.source.get("transport", "tcp")
            os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = f"rtsp_transport;{transport}|fflags;nobuffer|flags;low_delay"
            params = [cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, 6000, cv2.CAP_PROP_READ_TIMEOUT_MSEC, 4000]
            return cv2.VideoCapture(value, cv2.CAP_FFMPEG, params)
        if kind == "file":
            os.environ.pop("OPENCV_FFMPEG_CAPTURE_OPTIONS", None)
            return cv2.VideoCapture(value)
        backends = (cv2.CAP_DSHOW, cv2.CAP_MSMF) if sys.platform == "win32" else (cv2.CAP_ANY,)
        for backend in backends:
            capture = cv2.VideoCapture(int(value), backend)
            if capture.isOpened():
                size = self.source.get("resolution")
                if size:
                    capture.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
                    capture.set(cv2.CAP_PROP_FRAME_WIDTH, size[0])
                    capture.set(cv2.CAP_PROP_FRAME_HEIGHT, size[1])
                capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                return capture
            capture.release()
        return cv2.VideoCapture()

    def run(self):
        attempt = 0
        while not self.stopped.is_set():
            attempt += 1
            self.status = "Подключение…" if attempt == 1 else f"Переподключение, попытка {attempt}…"
            capture = self._open()
            if not capture.isOpened():
                capture.release()
                self.stopped.wait(min(5.0, 1.0 + attempt * 0.5))
                continue
            is_file = self.source["kind"] == "file"
            file_delay = 1.0 / (capture.get(cv2.CAP_PROP_FPS) or 25) if is_file else 0
            counter, counter_start = 0, time.monotonic()
            while not self.stopped.is_set():
                started = time.monotonic()
                ok, frame = capture.read()
                if not ok or frame is None:
                    if is_file and self.frame_count:
                        capture.set(cv2.CAP_PROP_POS_FRAMES, 0)  # Видеофайл крутится по кругу.
                        continue
                    break
                attempt = 0
                self.status = "Есть сигнал"
                with self.lock:
                    self.frame, self.frame_time = frame, time.monotonic()
                    self.frame_count += 1
                counter += 1
                if time.monotonic() - counter_start >= 1:
                    self.fps = counter / (time.monotonic() - counter_start)
                    counter, counter_start = 0, time.monotonic()
                if file_delay:
                    self.stopped.wait(max(0.0, file_delay - (time.monotonic() - started)))
            capture.release()
            self.stopped.wait(1.0)


def _font(size, bold=False):
    names = (["segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf"] if bold
             else ["segoeui.ttf", "arial.ttf", "DejaVuSans.ttf", "LiberationSans-Regular.ttf"])
    folders = [os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "Fonts"),
               "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/truetype/liberation",
               "/Library/Fonts", "/System/Library/Fonts/Supplemental"]
    for folder in folders:
        for name in names:
            path = os.path.join(folder, name)
            if os.path.exists(path):
                return ImageFont.truetype(path, size)
    return ImageFont.load_default(size)


def text_image(lines, size, color=(255, 247, 232), bold=False, background=None, padding=0):
    """Рисует текст с кириллицей (cv2.putText её не умеет) в BGRA-изображение."""
    font = _font(size, bold)
    spacing = int(size * 0.35)
    probe = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    box = probe.multiline_textbbox((0, 0), "\n".join(lines), font=font, spacing=spacing)
    width, height = box[2] - box[0] + padding * 2, box[3] - box[1] + padding * 2
    image = Image.new("RGBA", (max(1, width), max(1, height)), background or (0, 0, 0, 0))
    ImageDraw.Draw(image).multiline_text((padding - box[0], padding - box[1]), "\n".join(lines),
                                         font=font, fill=color + (255,), spacing=spacing)
    return cv2.cvtColor(np.array(image), cv2.COLOR_RGBA2BGRA)


def fit_width(image, width):
    if image.shape[1] <= width:
        return image
    return cv2.resize(image, (width, max(1, image.shape[0] * width // image.shape[1])), interpolation=cv2.INTER_AREA)


def overlay(canvas, image, x, y):
    height, width = image.shape[:2]
    x, y = max(0, x), max(0, y)
    height, width = min(height, canvas.shape[0] - y), min(width, canvas.shape[1] - x)
    if height <= 0 or width <= 0:
        return
    image = image[:height, :width]
    alpha = image[:, :, 3:4].astype(np.float32) / 255
    area = canvas[y:y + height, x:x + width]
    area[:] = (image[:, :, :3] * alpha + area * (1 - alpha)).astype(np.uint8)


class Viewer:
    def __init__(self, source, settings, monitor):
        self.source = source
        self.settings = settings
        self.monitor = monitor  # dict: x, y, width, height
        self.fullscreen = settings.get("fullscreen", True)
        self.rotation = settings.get("rotation", 0)
        self.mirror = settings.get("mirror", False)
        self.show_logo = settings.get("logo", True)
        self.zoom = 1.0
        self.show_help = False
        self.cache = {}
        logo = cv2.imread(resource("logo.png"), cv2.IMREAD_UNCHANGED)
        self.logo = logo if logo is not None and logo.shape[2] == 4 else None

    def _cached(self, key, build):
        if len(self.cache) > 60:  # При изменении размера окна старые картинки больше не нужны.
            self.cache.clear()
        if key not in self.cache:
            self.cache[key] = build()
        return self.cache[key]

    def _logo(self, height):
        if self.logo is None:
            return None
        def build():
            width = int(self.logo.shape[1] * height / self.logo.shape[0])
            return cv2.resize(self.logo, (width, height), interpolation=cv2.INTER_AREA if height < self.logo.shape[0] else cv2.INTER_CUBIC)
        return self._cached(("logo", height), build)

    def _transform(self, frame):
        if self.mirror:
            frame = cv2.flip(frame, 1)
        if self.rotation:
            frame = cv2.rotate(frame, {90: cv2.ROTATE_90_CLOCKWISE, 180: cv2.ROTATE_180,
                                       270: cv2.ROTATE_90_COUNTERCLOCKWISE}[self.rotation])
        if self.zoom > 1:
            height, width = frame.shape[:2]
            crop_h, crop_w = int(height / self.zoom), int(width / self.zoom)
            top, left = (height - crop_h) // 2, (width - crop_w) // 2
            frame = frame[top:top + crop_h, left:left + crop_w]
        return frame

    def render(self, frame, width, height, standby):
        canvas = np.zeros((height, width, 3), np.uint8)
        if standby:
            canvas[:] = INK
            logo = self._logo(max(40, height // 6))
            message = self._cached(("standby", height), lambda: text_image(
                ["Ожидание сигнала с прибора…"], max(14, height // 28), color=(185, 200, 172)))
            if logo is not None:
                overlay(canvas, logo, (width - logo.shape[1]) // 2, height // 2 - logo.shape[0])
            overlay(canvas, message, (width - message.shape[1]) // 2, height // 2 + height // 18)
        else:
            frame = self._transform(frame)
            frame_h, frame_w = frame.shape[:2]
            scale = min(width / frame_w, height / frame_h)
            new_w, new_h = max(1, round(frame_w * scale)), max(1, round(frame_h * scale))
            resized = cv2.resize(frame, (new_w, new_h), interpolation=cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC)
            top, left = (height - new_h) // 2, (width - new_w) // 2
            canvas[top:top + new_h, left:left + new_w] = resized
            margin = max(10, height // 30)
            if self.show_logo:
                logo = self._logo(max(28, height // 14))
                if logo is not None:
                    overlay(canvas, logo, width - logo.shape[1] - margin, margin)
            caption = self.settings.get("caption", "").strip()
            if caption:
                image = self._cached(("caption", caption, width, height), lambda: fit_width(text_image(
                    [caption], max(14, height // 26), bold=True, background=INK[::-1] + (215,), padding=max(8, height // 70)), width - 2 * margin))
                overlay(canvas, image, (width - image.shape[1]) // 2, height - image.shape[0] - margin)
        if self.show_help:
            self._draw_help(canvas, height)
        return canvas

    def _draw_help(self, canvas, height):
        source = self.source.source
        info = {"url": source["value"], "file": os.path.basename(source["value"]),
                "device": f"устройство {source.get('name') or source['value']}"}[source["kind"]]
        frame, _, _ = self.source.latest()
        size = f"{frame.shape[1]}×{frame.shape[0]}" if frame is not None else "—"
        lines = [
            "Esc — остановить трансляцию     F или F11 — полный экран / окно",
            "R — повернуть на 90°     M — зеркально     L — логотип вкл/выкл",
            "+ / − — цифровое увеличение     0 — сбросить     H — скрыть подсказку",
            "",
            f"Источник: {info}",
            f"Состояние: {self.source.status}     Кадр: {size}     {self.source.fps:.0f} кадр/с",
        ]
        image = text_image(lines, max(12, height // 48), background=(0, 0, 0, 200), padding=max(10, height // 50))
        overlay(canvas, image, max(10, height // 30), max(10, height // 30))

    def _apply_window_mode(self):
        cv2.setWindowProperty(WINDOW, cv2.WND_PROP_FULLSCREEN, cv2.WINDOW_NORMAL)
        if self.fullscreen:
            cv2.moveWindow(WINDOW, self.monitor["x"], self.monitor["y"])
            cv2.setWindowProperty(WINDOW, cv2.WND_PROP_FULLSCREEN, cv2.WINDOW_FULLSCREEN)
        else:
            cv2.resizeWindow(WINDOW, self.monitor["width"] * 2 // 3, self.monitor["height"] * 2 // 3)
            cv2.moveWindow(WINDOW, self.monitor["x"] + self.monitor["width"] // 6, self.monitor["y"] + self.monitor["height"] // 6)
        _show_cursor(not self.fullscreen)

    def _size(self):
        width, height = self.monitor["width"], self.monitor["height"]
        if not self.fullscreen:
            try:
                _, _, window_w, window_h = cv2.getWindowImageRect(WINDOW)
                if window_w > 50 and window_h > 50:
                    width, height = window_w, window_h
            except cv2.error:
                pass
        if width > MAX_RENDER_WIDTH:  # 4K-экран: рисуем в Full HD, окно растянет само. Видео с прибора меньше.
            width, height = MAX_RENDER_WIDTH, round(height * MAX_RENDER_WIDTH / width)
        return width, height

    def _handle_key(self, key):
        """Возвращает False, если нужно выйти. Учитывает русскую раскладку."""
        if key in (-1, 255):
            return True
        if key == 27:
            return False
        if key in (0x7A0000, 0xFFC8):  # F11 в Windows и в X11
            self.fullscreen = not self.fullscreen
            self._apply_window_mode()
            return True
        char = chr(key & 0xFFFF).lower()
        actions = {
            "f": "fullscreen", "а": "fullscreen", "r": "rotate", "к": "rotate", "m": "mirror", "ь": "mirror",
            "l": "logo", "д": "logo", "h": "help", "р": "help", "+": "in", "=": "in", "-": "out", "_": "out", "0": "reset",
        }
        action = actions.get(char)
        if action == "fullscreen":
            self.fullscreen = not self.fullscreen
            self._apply_window_mode()
        elif action == "rotate":
            self.rotation = (self.rotation + 90) % 360
        elif action == "mirror":
            self.mirror = not self.mirror
        elif action == "logo":
            self.show_logo = not self.show_logo
        elif action == "help":
            self.show_help = not self.show_help
        elif action == "in":
            self.zoom = min(4.0, self.zoom * 1.25)
        elif action == "out":
            self.zoom = max(1.0, self.zoom / 1.25)
        elif action == "reset":
            self.zoom, self.rotation, self.mirror = 1.0, 0, False
        return True

    def run(self, exit_after=None):
        """Показывает видео, пока не нажата Esc или не закрыто окно. Возвращает итоговые настройки."""
        self.source.start()
        cv2.namedWindow(WINDOW, cv2.WINDOW_NORMAL)
        cv2.resizeWindow(WINDOW, 640, 360)
        self._apply_window_mode()
        started, last_count, last_state, last_draw = time.monotonic(), -1, None, 0.0
        try:
            while True:
                frame, frame_time, count = self.source.latest()
                standby = frame is None or time.monotonic() - frame_time > STANDBY_AFTER
                size = self._size()
                state = (standby, size, self.rotation, self.mirror, self.show_logo, self.zoom, self.show_help, self.fullscreen)
                # Перерисовываем при новом кадре или изменении настроек; подсказку с состоянием — дважды в секунду.
                if count != last_count or state != last_state or (self.show_help and time.monotonic() - last_draw > 0.5):
                    cv2.imshow(WINDOW, self.render(frame, size[0], size[1], standby))
                    last_count, last_state, last_draw = count, state, time.monotonic()
                if not self._handle_key(cv2.waitKeyEx(5)):
                    break
                if cv2.getWindowProperty(WINDOW, cv2.WND_PROP_VISIBLE) < 1:
                    break
                if exit_after and time.monotonic() - started > exit_after:
                    break
        finally:
            self.source.stop()
            self.source.join(timeout=6)  # Даём потоку закрыть захват, иначе OpenCV падает при выходе.
            _show_cursor(True)
            cv2.destroyWindow(WINDOW)
            cv2.waitKey(1)
        return {"rotation": self.rotation, "mirror": self.mirror, "logo": self.show_logo}


_cursor_hidden = False


def _show_cursor(visible):
    """Прячет курсор мыши над окном трансляции в Windows."""
    global _cursor_hidden
    if sys.platform != "win32" or _cursor_hidden == (not visible):
        return
    try:
        import ctypes
        ctypes.windll.user32.ShowCursor(bool(visible))
        _cursor_hidden = not visible
    except (AttributeError, OSError):
        pass
