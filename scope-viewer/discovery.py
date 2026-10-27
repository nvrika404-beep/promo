"""Поиск видеопотока прибора в Wi-Fi-сети, которую раздаёт сам прибор."""
import ipaddress
import re
import socket
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

RTSP_PORTS = (554, 8554)
# Распространённые пути RTSP у тепловизоров и камер. Точный путь зависит от модели.
RTSP_PATHS = (
    "/", "/live", "/live/main", "/live/ch0", "/live/av0", "/livestream", "/stream", "/stream0",
    "/stream1", "/ch0", "/ch1", "/ch01.264", "/0", "/1", "/11", "/main", "/video", "/video1",
    "/h264", "/av0_0", "/media/video1", "/onvif1", "/Streaming/Channels/101",
    "/cam/realmonitor?channel=1&subtype=0",
)


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
    """Прибор обычно сам является точкой доступа, то есть шлюзом сети."""
    hosts = gateways()
    for address in local_addresses():
        network = ipaddress.IPv4Network(f"{address}/24", strict=False)
        for last in (1, 254, 2, 100, 123):
            host = str(network.network_address + last)
            if host != address:
                hosts.append(host)
    return list(dict.fromkeys(hosts))


def port_open(host, port, timeout=0.6):
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def rtsp_describe(url, timeout=1.5):
    """Возвращает (код ответа, есть ли видео в описании потока) или (None, False)."""
    match = re.match(r"rtsp://([^/:]+)(?::(\d+))?", url)
    host, port = match.group(1), int(match.group(2) or 554)
    request = (f"DESCRIBE {url} RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\n"
               "User-Agent: RikaScopeView\r\n\r\n").encode()
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
    status = re.match(r"RTSP/1\.0 (\d{3})", text)
    return (int(status.group(1)) if status else None), "m=video" in text


def discover(progress=lambda message: None):
    """Ищет RTSP-поток. Возвращает (url или None, понятное сообщение)."""
    hosts = candidate_hosts()
    if not hosts:
        return None, "Компьютер не подключён ни к одной сети. Подключитесь к Wi-Fi прибора."
    protected = None
    progress(f"Проверяем адреса в сети: {', '.join(hosts[:4])}…")
    targets = [(host, port) for host in hosts for port in RTSP_PORTS]
    with ThreadPoolExecutor(max_workers=16) as pool:
        servers = [target for target, is_open in zip(targets, pool.map(lambda t: port_open(*t), targets)) if is_open]
    if not servers:
        return None, ("Видеопоток не найден. Проверьте, что Wi-Fi на приборе включён и компьютер "
                      "подключён к его сети. Если прибор отдаёт видео только в своё приложение, "
                      "используйте видеовыход прибора и карту видеозахвата.")
    for host, port in servers:
        base = f"rtsp://{host}" if port == 554 else f"rtsp://{host}:{port}"
        for path in RTSP_PATHS:
            url = base + path
            progress(f"Пробуем {url}")
            status, has_video = rtsp_describe(url)
            if status == 200 and has_video:
                return url, f"Найден видеопоток: {url}"
            if status == 401 and protected is None:
                protected = url
    if protected:
        return None, (f"Поток найден ({protected}), но прибор требует логин и пароль. "
                      "Впишите адрес в виде rtsp://логин:пароль@" + protected[len("rtsp://"):])
    host, port = servers[0]
    return None, (f"На {host}:{port} есть RTSP-сервер, но путь к видео подобрать не удалось. "
                  "Узнайте адрес потока в инструкции прибора и впишите его вручную.")
