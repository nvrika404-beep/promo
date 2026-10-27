@echo off
rem Запуск из исходников: нужен Python 3.10+ с https://python.org
cd /d "%~dp0"
python -m pip install -q -r requirements.txt
python scope_viewer.py %*
