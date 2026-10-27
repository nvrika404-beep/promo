"""RikaNV — трансляция изображения с прибора на телевизор или проектор."""
import argparse
import json
import os
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import cv2

from discovery import discover
from viewer import FrameSource, Viewer, resource

APP_NAME = "RikaNV — трансляция с прибора"
RESOLUTIONS = {"Авто": None, "640×480": (640, 480), "1280×720": (1280, 720), "1920×1080": (1920, 1080)}
DEFAULTS = {
    "kind": "url", "url": "", "transport": "tcp", "device": 0, "device_name": "", "resolution": "Авто",
    "file": "", "monitor": None, "logo": True, "caption": "", "mirror": False, "rotation": 0, "autostart": False,
}


def config_path():
    base = os.environ.get("APPDATA") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(base, "RikaScopeView", "config.json")


def load_config():
    try:
        with open(config_path(), encoding="utf-8") as file:
            return {**DEFAULTS, **json.load(file)}
    except (OSError, ValueError):
        return dict(DEFAULTS)


def save_config(config):
    try:
        os.makedirs(os.path.dirname(config_path()), exist_ok=True)
        with open(config_path(), "w", encoding="utf-8") as file:
            json.dump(config, file, ensure_ascii=False, indent=2)
    except OSError:
        pass  # Настройки не сохранятся, но трансляция работает.


def list_monitors():
    try:
        from screeninfo import get_monitors
        monitors = [{"x": m.x, "y": m.y, "width": m.width, "height": m.height, "primary": bool(m.is_primary)}
                    for m in get_monitors()]
    except Exception:  # screeninfo может не найти экраны в необычных системах.
        monitors = []
    return monitors or [{"x": 0, "y": 0, "width": 1920, "height": 1080, "primary": True}]


def monitor_label(index, monitor, count):
    note = " — единственный экран" if count == 1 else " — основной (ноутбук)" if monitor["primary"] else " — телевизор/проектор"
    return f"Экран {index + 1}: {monitor['width']}×{monitor['height']}{note}"


def list_devices():
    """Названия камер и карт захвата. В Windows берём имена из DirectShow, иначе перебираем номера."""
    if sys.platform == "win32":
        try:
            from pygrabber.dshow_graph import FilterGraph
            names = FilterGraph().get_input_devices()
            if names:
                return list(enumerate(names))
        except Exception:
            pass
    found = []
    for index in range(6):
        capture = cv2.VideoCapture(index, cv2.CAP_DSHOW if sys.platform == "win32" else cv2.CAP_ANY)
        if capture.isOpened():
            found.append((index, f"Устройство {index}"))
        capture.release()
    return found


def build_source(config):
    if config["kind"] == "url":
        return {"kind": "url", "value": config["url"].strip(), "transport": config["transport"]}
    if config["kind"] == "file":
        return {"kind": "file", "value": config["file"]}
    return {"kind": "device", "value": int(config["device"]), "name": config.get("device_name"),
            "resolution": RESOLUTIONS.get(config["resolution"])}


def pick_monitor(config, monitors):
    index = config.get("monitor")
    if isinstance(index, int) and 0 <= index < len(monitors):
        return monitors[index]
    secondary = [m for m in monitors if not m["primary"]]
    return (secondary or monitors)[0]


def problem(config):
    if config["kind"] == "url" and not config["url"].strip().lower().startswith(("rtsp://", "rtmp://", "http://", "https://", "udp://")):
        return "Укажите адрес видеопотока или нажмите «Найти прибор»."
    if config["kind"] == "file" and not os.path.isfile(config["file"]):
        return "Выберите видеофайл."
    return None


def run_viewer(config, exit_after=None, persist=True):
    monitor = pick_monitor(config, list_monitors())
    viewer = Viewer(FrameSource(build_source(config)), config, monitor)
    config.update(viewer.run(exit_after=exit_after))
    if persist:
        save_config({key: value for key, value in config.items() if key != "fullscreen"})


class Launcher:
    def __init__(self, root, config):
        self.root, self.config = root, config
        self.monitors = list_monitors()
        self.devices = []
        root.title(APP_NAME)
        root.resizable(False, False)
        try:
            root.iconphoto(True, tk.PhotoImage(file=resource("logo.png")))
        except tk.TclError:
            pass
        style = ttk.Style()
        style.configure("Big.TButton", font=("Segoe UI", 13, "bold"), padding=(18, 12))
        style.configure("Hint.TLabel", foreground="#5d6b55")
        page = ttk.Frame(root, padding=18)
        page.grid(sticky="nsew")
        page.columnconfigure(0, weight=1)

        self.kind = tk.StringVar(value=config["kind"])
        source = ttk.LabelFrame(page, text=" 1. Откуда брать видео ", padding=12)
        source.grid(row=0, column=0, sticky="ew")
        source.columnconfigure(1, weight=1)

        ttk.Radiobutton(source, text="Wi-Fi прибора", variable=self.kind, value="url").grid(row=0, column=0, sticky="w")
        self.url = tk.StringVar(value=config["url"])
        ttk.Entry(source, textvariable=self.url, width=46).grid(row=0, column=1, sticky="ew", padx=8)
        self.find_button = ttk.Button(source, text="Найти прибор", command=self.find)
        self.find_button.grid(row=0, column=2)
        self.transport = tk.StringVar(value=config["transport"])
        transport_row = ttk.Frame(source)
        transport_row.grid(row=1, column=1, columnspan=2, sticky="w", padx=8, pady=(4, 0))
        ttk.Label(transport_row, text="Передача:").pack(side="left")
        ttk.Radiobutton(transport_row, text="надёжная (TCP)", variable=self.transport, value="tcp").pack(side="left", padx=6)
        ttk.Radiobutton(transport_row, text="быстрая (UDP)", variable=self.transport, value="udp").pack(side="left")
        self.find_status = tk.StringVar(value="Подключите компьютер к Wi-Fi прибора и нажмите «Найти прибор».")
        ttk.Label(source, textvariable=self.find_status, style="Hint.TLabel", wraplength=420).grid(
            row=2, column=1, sticky="w", padx=8, pady=(2, 10))
        self.report = None
        self.report_button = ttk.Button(source, text="Отчёт", command=self.show_report)

        ttk.Radiobutton(source, text="USB или карта захвата", variable=self.kind, value="device").grid(row=3, column=0, sticky="w")
        self.device = ttk.Combobox(source, state="readonly", width=44)
        self.device.grid(row=3, column=1, sticky="ew", padx=8)
        ttk.Button(source, text="Обновить", command=self.refresh_devices).grid(row=3, column=2)
        resolution_row = ttk.Frame(source)
        resolution_row.grid(row=4, column=1, columnspan=2, sticky="w", padx=8, pady=(4, 10))
        ttk.Label(resolution_row, text="Разрешение:").pack(side="left")
        self.resolution = ttk.Combobox(resolution_row, state="readonly", width=12, values=list(RESOLUTIONS))
        self.resolution.set(config["resolution"] if config["resolution"] in RESOLUTIONS else "Авто")
        self.resolution.pack(side="left", padx=6)

        ttk.Radiobutton(source, text="Видеофайл (запасной)", variable=self.kind, value="file").grid(row=5, column=0, sticky="w")
        self.file = tk.StringVar(value=config["file"])
        ttk.Entry(source, textvariable=self.file, width=46).grid(row=5, column=1, sticky="ew", padx=8)
        ttk.Button(source, text="Выбрать…", command=self.choose_file).grid(row=5, column=2)

        screen = ttk.LabelFrame(page, text=" 2. Куда выводить ", padding=12)
        screen.grid(row=1, column=0, sticky="ew", pady=12)
        self.monitor = ttk.Combobox(screen, state="readonly", width=48,
                                    values=[monitor_label(i, m, len(self.monitors)) for i, m in enumerate(self.monitors)])
        self.monitor.current(self.monitors.index(pick_monitor(config, self.monitors)))
        self.monitor.grid(row=0, column=0, sticky="w")
        ttk.Button(screen, text="Обновить", command=self.refresh_monitors).grid(row=0, column=1, padx=8)
        ttk.Label(screen, text="Телевизор подключите по HDMI. В Windows: Win+P → «Расширить».",
                  style="Hint.TLabel").grid(row=1, column=0, columnspan=2, sticky="w", pady=(6, 0))

        look = ttk.LabelFrame(page, text=" 3. Оформление ", padding=12)
        look.grid(row=2, column=0, sticky="ew")
        self.logo = tk.BooleanVar(value=config["logo"])
        self.mirror = tk.BooleanVar(value=config["mirror"])
        ttk.Checkbutton(look, text="Логотип RikaNV в углу", variable=self.logo).grid(row=0, column=0, sticky="w")
        ttk.Checkbutton(look, text="Отразить зеркально", variable=self.mirror).grid(row=0, column=1, sticky="w", padx=16)
        ttk.Label(look, text="Поворот:").grid(row=0, column=2, sticky="e")
        self.rotation = ttk.Combobox(look, state="readonly", width=6, values=["0°", "90°", "180°", "270°"])
        self.rotation.set(f"{config['rotation']}°")
        self.rotation.grid(row=0, column=3, padx=6)
        ttk.Label(look, text="Подпись внизу экрана (необязательно):").grid(row=1, column=0, columnspan=4, sticky="w", pady=(10, 2))
        self.caption = tk.StringVar(value=config["caption"])
        ttk.Entry(look, textvariable=self.caption, width=60).grid(row=2, column=0, columnspan=4, sticky="ew")

        self.autostart = tk.BooleanVar(value=config["autostart"])
        ttk.Checkbutton(page, text="Сразу начинать трансляцию при запуске программы", variable=self.autostart).grid(
            row=3, column=0, sticky="w", pady=(12, 0))
        ttk.Button(page, text="▶  Начать трансляцию", style="Big.TButton", command=self.start).grid(
            row=4, column=0, sticky="ew", pady=(12, 4))
        ttk.Label(page, text="Esc — остановить и вернуться сюда. H — подсказка по клавишам.",
                  style="Hint.TLabel").grid(row=5, column=0)
        self.refresh_devices()

    def refresh_devices(self):
        self.device.set("Поиск устройств…")
        def work():
            devices = list_devices()
            self.root.after(0, lambda: self._show_devices(devices))
        threading.Thread(target=work, daemon=True).start()

    def _show_devices(self, devices):
        self.devices = devices
        self.device["values"] = [name for _, name in devices]
        if not devices:
            self.device.set("Устройства не найдены")
            return
        saved = [i for i, (index, name) in enumerate(devices) if name == self.config.get("device_name")]
        self.device.current(saved[0] if saved else 0)

    def refresh_monitors(self):
        self.monitors = list_monitors()
        self.monitor["values"] = [monitor_label(i, m, len(self.monitors)) for i, m in enumerate(self.monitors)]
        self.monitor.current(self.monitors.index(pick_monitor({"monitor": None}, self.monitors)))

    def choose_file(self):
        path = filedialog.askopenfilename(title="Видео для показа",
                                          filetypes=[("Видео", "*.mp4 *.mov *.avi *.mkv *.wmv"), ("Все файлы", "*.*")])
        if path:
            self.file.set(path)
            self.kind.set("file")

    def find(self):
        self.find_button.state(["disabled"])
        self.report_button.grid_remove()
        self.kind.set("url")
        def progress(message):
            self.root.after(0, lambda: self.find_status.set(message))
        def work():
            url, message, report = discover(progress)
            self.root.after(0, lambda: self._found(url, message, report))
        threading.Thread(target=work, daemon=True).start()

    def _found(self, url, message, report):
        self.find_button.state(["!disabled"])
        self.find_status.set(message)
        if url:
            self.url.set(url)
        self.report = report
        if report:
            self.report_button.grid(row=2, column=2, sticky="n", pady=(2, 10))

    def show_report(self):
        window = tk.Toplevel(self.root)
        window.title("Отчёт о приборе")
        text = tk.Text(window, width=90, height=22, wrap="word", font=("Consolas", 10))
        text.insert("1.0", self.report)
        text.configure(state="disabled")
        text.pack(fill="both", expand=True, padx=12, pady=(12, 6))
        def copy():
            self.root.clipboard_clear()
            self.root.clipboard_append(self.report)
            copy_button.configure(text="Скопировано ✓")
        copy_button = ttk.Button(window, text="Скопировать отчёт", command=copy)
        copy_button.pack(pady=(0, 12))

    def collect(self):
        selected = self.device.current()
        device = self.devices[selected] if 0 <= selected < len(self.devices) else (0, "")
        self.config.update({
            "kind": self.kind.get(), "url": self.url.get(), "transport": self.transport.get(),
            "device": device[0], "device_name": device[1], "resolution": self.resolution.get(),
            "file": self.file.get(), "monitor": self.monitor.current(), "logo": self.logo.get(),
            "caption": self.caption.get(), "mirror": self.mirror.get(),
            "rotation": int(self.rotation.get().rstrip("°")), "autostart": self.autostart.get(),
        })
        return self.config

    def start(self):
        config = self.collect()
        if config["kind"] == "device" and not self.devices:
            messagebox.showwarning(APP_NAME, "Устройство не найдено. Подключите карту захвата или прибор по USB и нажмите «Обновить».")
            return
        error = problem(config)
        if error:
            messagebox.showwarning(APP_NAME, error)
            return
        self.root.withdraw()
        try:
            run_viewer(config)
        except Exception as error:  # Показываем причину вместо молчаливого закрытия.
            messagebox.showerror(APP_NAME, f"Не удалось показать видео: {error}")
        finally:
            self.root.deiconify()
            self.mirror.set(config["mirror"])
            self.logo.set(config["logo"])
            self.rotation.set(f"{config['rotation']}°")


def main():
    if sys.platform == "win32":
        try:  # Реальные пиксели экрана вместо растянутой картинки при масштабе 125–150%.
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except (AttributeError, OSError):
            pass
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--auto", action="store_true", help="сразу начать трансляцию с сохранёнными настройками")
    parser.add_argument("--source", help="адрес потока (rtsp://…), номер устройства или путь к видеофайлу")
    parser.add_argument("--monitor", type=int, help="номер экрана, начиная с 1")
    parser.add_argument("--windowed", action="store_true", help="показывать в окне, а не на весь экран")
    parser.add_argument("--exit-after", type=float, help=argparse.SUPPRESS)  # для проверки
    args = parser.parse_args()

    config = load_config()
    if args.source:
        if args.source.isdigit():
            config.update(kind="device", device=int(args.source), device_name="")
        elif os.path.isfile(args.source):
            config.update(kind="file", file=args.source)
        else:
            config.update(kind="url", url=args.source)
    if args.monitor:
        config["monitor"] = args.monitor - 1
    if args.windowed:
        config["fullscreen"] = False

    if args.source or args.auto or (config["autostart"] and not problem(config)):
        run_viewer(config, exit_after=args.exit_after, persist=not args.source)
        if args.source or args.auto:
            return
    root = tk.Tk()
    Launcher(root, config)
    root.mainloop()


if __name__ == "__main__":
    main()
