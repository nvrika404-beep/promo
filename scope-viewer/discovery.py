"""Поиск видеопотока прибора в Wi-Fi-сети, которую раздаёт сам прибор."""
import ipaddress
import re
import socket
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from ftplib import FTP, error_perm

RTSP_PORTS = (554, 8554)
# Распространённые пути RTSP у тепловизоров и камер. Точный путь зависит от модели.
RTSP_PATHS = (
    "/1/h264major", "/1/h264minor", "/0/h264major", "/0/h264minor",  # приборы RikaNV («Кабан»)
    "/", "/live", "/live/main", "/live/ch0", "/live/av0", "/livestream", "/stream", "/stream0",
    "/stream1", "/ch0", "/ch1", "/ch01.264", "/0", "/1", "/11", "/main", "/video", "/video1",
    "/h264", "/av0_0", "/media/video1", "/onvif1", "/Streaming/Channels/101",
    "/cam/realmonitor?channel=1&subtype=0", "/live.sdp", "/live/0", "/live/1", "/live0", "/live1",
    "/live/stream0", "/live/stream1", "/live/ch00_0", "/live/ch01_0", "/stream/0", "/stream/main",
    "/stream=0", "/video0", "/videoMain", "/mainstream", "/main_stream", "/substream", "/preview",
    "/ch0_0.h264", "/media/stream1", "/channel1", "/tcp/av0_0", "/h264/ch1/main/av_stream",
    "/user=admin&password=&channel=1&stream=0.sdp", "/ir", "/ir.sdp", "/thermal", "/rtsp", "/test",
)
# Медиасервер ZLMediaKit (стоит в части приборов) отдаёт потоки по адресам вида /приложение/поток.
ZLM_STREAMS = ("stream", "test", "0", "1", "main", "sub", "ch0", "ch1", "video", "ir", "tv", "cam", "camera",
               "thermal", "preview", "live", "av0", "stream0", "stream1", "mainstream", "substream", "h264", "rtsp")
RTSP_PATHS = tuple(dict.fromkeys(RTSP_PATHS + tuple(f"/live/{name}" for name in ZLM_STREAMS)
                                 + ("/rtp/0", "/app/stream", "/stream/live", "/video/0")))
# ZLMediaKit ждёт несуществующий поток до 15 секунд и лишь потом отвечает 404, поэтому ждём дольше.
SLOW_TIMEOUT = 18
REPORT_PORTS = (21, 23, 80, 443, 554, 1935, 5000, 6000, 7070, 8000, 8080, 8081, 8554, 8888, 9000, 34567)


def _run(command):
    try:
        flags = 0x08000000 if sys.platform == "win32" else 0  # CREATE_NO_WINDOW
        return subprocess.run(command, capture_output=True, timeout=5, creationflags=flags).stdout.decode(errors="ignore")
    except (OSError, subprocess.SubprocessError):
        return ""


def _private_ipv4(text):
    found = []
    for match in re.findall(r"\b(?:\d{1,3}\.){3}\d{1,3}\b", text):
        try:
            address = ipaddress.IPv4Address(match)
        except ValueError:
            continue
        if address.is_private and not address.is_loopback and match not in found:
            found.append(match)
    return found


def local_addresses():
    addresses = []
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            addresses.append(info[4][0])
    except OSError:
        pass
    try:  # Адрес, с которого система отправила бы пакет: без реальной отправки.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("10.254.254.254", 1))
            addresses.append(probe.getsockname()[0])
    except OSError:
        pass
    if sys.platform == "win32":
        addresses += _private_ipv4("\n".join(line for line in _run(["ipconfig"]).splitlines() if "IPv4" in line))
    else:
        addresses += _private_ipv4(_run(["ip", "-4", "-o", "addr"]))
    return [a for a in dict.fromkeys(addresses) if a in _private_ipv4(a)]


def gateways():
    if sys.platform == "win32":
        text = "\n".join(re.findall(r"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\S+)", _run(["route", "print", "-4"]), re.M))
    else:
        text = "\n".join(re.findall(r"default via (\S+)", _run(["ip", "-4", "route"])))
    return _private_ipv4(text)


def candidate_hosts():
    """Все адреса в сетях компьютера. Прибор обычно сам точка доступа, поэтому шлюз и .1 проверяем первыми."""
    own = local_addresses()
    preferred, rest = list(gateways()), []
    for address in own:
        network = ipaddress.IPv4Network(f"{address}/24", strict=False)
        for host in network.hosts():
            last = int(str(host).rsplit(".", 1)[1])
            (preferred if last in (1, 254, 123, 100, 10) else rest).append(str(host))
    return [host for host in dict.fromkeys(preferred + rest) if host not in own]


def scan(targets, timeout=0.5):
    """Параллельно проверяет пары (адрес, порт). Возвращает открытые в исходном порядке."""
    targets = list(targets)
    with ThreadPoolExecutor(max_workers=128) as pool:
        flags = list(pool.map(lambda target: port_open(*target, timeout=timeout), targets))
    return [target for target, is_open in zip(targets, flags) if is_open]


def is_proxy(host):
    """VPN и прокси «принимают» соединения на любой порт. Настоящий прибор на портах 1 и 65001 не отвечает."""
    return port_open(host, 1, 0.5) and port_open(host, 65001, 0.5)


def port_open(host, port, timeout=0.6):
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def rtsp_request(url, method="DESCRIBE", timeout=1.5):
    """Отправляет один RTSP-запрос. Возвращает (код ответа или None, полный текст ответа)."""
    match = re.match(r"rtsp://([^/:]+)(?::(\d+))?", url)
    host, port = match.group(1), int(match.group(2) or 554)
    accept = "Accept: application/sdp\r\n" if method == "DESCRIBE" else ""
    request = f"{method} {url} RTSP/1.0\r\nCSeq: 2\r\n{accept}User-Agent: RikaScopeView\r\n\r\n".encode()
    data = b""
    try:
        with socket.create_connection((host, port), timeout=timeout) as connection:
            connection.settimeout(timeout)
            connection.sendall(request)
            while len(data) < 65536:
                chunk = connection.recv(4096)
                if not chunk:
                    break
                data += chunk
                header, separator, body = data.partition(b"\r\n\r\n")
                if separator:
                    length = re.search(rb"(?im)^content-length:\s*(\d+)", header)
                    if not length or len(body) >= int(length.group(1)):
                        break
    except OSError:
        pass  # Используем то, что успели получить.
    text = data.decode(errors="ignore")
    status = re.match(r"RTSP/1\.\d (\d{3})", text)
    return (int(status.group(1)) if status else None), text


def rtsp_describe(url, timeout=1.5):
    """Возвращает (код ответа, есть ли видео в описании потока)."""
    status, text = rtsp_request(url, timeout=timeout)
    return status, "m=video" in text


def _header(text, name):
    match = re.search(rf"(?im)^{name}:\s*(.+?)\s*$", text)
    return match.group(1) if match else "—"


def http_banner(host, port):
    try:
        with socket.create_connection((host, port), timeout=1.5) as connection:
            connection.settimeout(1.5)
            connection.sendall(f"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n".encode())
            data = b""
            while len(data) < 32768:
                chunk = connection.recv(4096)
                if not chunk:
                    break
                data += chunk
    except OSError:
        pass
    data = data.decode(errors="ignore")
    if not data:
        return "нет ответа"
    title = re.search(r"(?is)<title>(.*?)</title>", data)
    hints = sorted(set(re.findall(r"rtsp://[^\s\"'<>]+|[\w./-]+\.(?:cgi|cfg|ini|json|xml)\b", data)))[:15]
    return (f"{data.splitlines()[0]}; Server: {_header(data, 'Server')}"
            + (f"; title: {title.group(1).strip()[:60]}" if title else "")
            + (f"; ссылки: {', '.join(hints)}" if hints else ""))


def probe_paths(base, stop_on_video=True, progress=lambda message: None):
    """Запрашивает все пути одновременно. Возвращает {путь: (код, ответ, секунд до ответа)}."""
    results = {}
    def probe(path):
        started = time.monotonic()
        status, text = rtsp_request(base + path, timeout=SLOW_TIMEOUT)
        return path, status, text, time.monotonic() - started
    pool = ThreadPoolExecutor(max_workers=len(RTSP_PATHS))  # Все сразу: ожидание у прибора идёт параллельно.
    try:
        futures = [pool.submit(probe, path) for path in RTSP_PATHS]
        for done, future in enumerate(as_completed(futures), 1):
            path, status, text, elapsed = future.result()
            results[path] = (status, text, elapsed)
            progress(f"Проверено {done} из {len(futures)} вариантов (прибор отвечает медленно, до 40 секунд)…")
            if stop_on_video and status == 200 and "m=video" in text:
                break
    finally:
        pool.shutdown(wait=False, cancel_futures=True)
    return results


def describe_rtsp(base, results=None):
    _, options = rtsp_request(base + "/", "OPTIONS")
    lines = [f"  RTSP {base}: сервер {_header(options, 'Server')}, методы {_header(options, 'Public')}"]
    codes = {}
    for path, (status, text, elapsed) in (results or probe_paths(base, stop_on_video=False)).items():
        codes.setdefault(status, []).append((path, elapsed))
        if status == 200:
            lines.append(f"    200 {path}: " + " | ".join(l for l in text.splitlines() if l.startswith(("m=", "a=control")))[:200])
    for status, items in codes.items():
        if status != 200:
            slowest = max(elapsed for _, elapsed in items)
            lines.append(f"    {status or 'нет ответа'}: {len(items)} адр. (до {slowest:.0f} с), например "
                         + ", ".join(path for path, _ in items[:4]))
    return lines


def ftp_listing(host):
    """Список файлов на FTP прибора без пароля: там бывают настройки с именами потоков."""
    try:
        with FTP(host, timeout=4) as ftp:
            ftp.login()
            names = ftp.nlst()
    except (OSError, EOFError, error_perm) as error:
        return f"не открылся ({str(error)[:60] or type(error).__name__})"
    return ", ".join(names[:40]) or "пусто"


def diagnose(proxies, rtsp_results=None):
    """Текстовый отчёт: какие устройства есть в сетях компьютера и что они отвечают. Нужен, чтобы подобрать адрес."""
    own = local_addresses()
    lines = [f"Компьютер: адреса {', '.join(own) or '—'}, шлюзы {', '.join(gateways()) or '—'}"]
    for address in own:
        network = ipaddress.IPv4Network(f"{address}/24", strict=False)
        hosts = [str(h) for h in network.hosts() if str(h) != address]
        if any(host in proxies for host in hosts) or is_proxy(hosts[0]):
            lines.append(f"Сеть {network}: похоже на VPN или прокси, пропущена")
            continue
        found = {}
        for host, port in scan(((h, p) for h in hosts for p in REPORT_PORTS), timeout=0.4):
            found.setdefault(host, []).append(port)
        lines.append(f"Сеть {network}: устройств {len(found)}")
        for host, ports in found.items():
            lines.append(f"  {host}: порты {', '.join(map(str, ports))}")
            for port in ports:
                if port in (80, 8080, 8000):
                    lines.append(f"    HTTP {port}: {http_banner(host, port)}")
                if port == 21:
                    lines.append(f"    FTP: {ftp_listing(host)}")
                if port in RTSP_PORTS:
                    base = f"rtsp://{host}" if port == 554 else f"rtsp://{host}:{port}"
                    lines += describe_rtsp(base, (rtsp_results or {}).get(base))
    return "\n".join(lines)


def discover(progress=lambda message: None):
    """Ищет RTSP-поток. Возвращает (url или None, понятное сообщение, отчёт для поддержки или None)."""
    hosts = candidate_hosts()
    if not hosts:
        return None, "Компьютер не подключён ни к одной сети. Подключитесь к Wi-Fi прибора.", None
    progress(f"Проверяем сеть: {len(hosts)} адресов…")
    found = scan((host, port) for host in hosts for port in RTSP_PORTS)
    proxies = {host for host in dict.fromkeys(host for host, _ in found) if is_proxy(host)}
    servers = [(host, port) for host, port in found if host not in proxies]
    protected = fallback = None
    rtsp_results = {}
    for host, port in servers:
        base = f"rtsp://{host}" if port == 554 else f"rtsp://{host}:{port}"
        progress(f"Найден видеосервер {base}, подбираем адрес потока…")
        rtsp_results[base] = probe_paths(base, progress=progress)
        for path in RTSP_PATHS:  # В порядке списка: сначала самые типичные адреса.
            status, text, _ = rtsp_results[base].get(path, (None, "", 0))
            url = base + path
            if status == 200 and "m=video" in text:
                return url, f"Найден видеопоток: {url}", None
            if status == 200 and "m=" in text and fallback is None:
                fallback = url
            if status == 401 and protected is None:
                protected = url
    if fallback:
        return fallback, f"Найден поток: {fallback}. Если картинки не будет, сообщите разработчику.", None
    if protected:
        return None, (f"Поток найден ({protected}), но прибор требует логин и пароль. "
                      "Впишите адрес в виде rtsp://логин:пароль@" + protected[len("rtsp://"):]), None
    progress("Собираем отчёт о сети прибора, до минуты…")
    vpn = " Похоже, на компьютере включён VPN: на время проверки лучше его выключить." if proxies else ""
    where = f"На {servers[0][0]}:{servers[0][1]} есть RTSP-сервер, но путь к видео подобрать не удалось." if servers \
        else "Видеопоток не найден."
    return None, (f"{where}{vpn} Нажмите «Отчёт», скопируйте его и пришлите разработчику."), diagnose(proxies, rtsp_results)
