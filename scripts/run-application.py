#!/usr/bin/env python3
import argparse
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parent.parent


def docker_connection(connection):
    fields = []
    start = 0
    quote = None
    index = 0
    while index < len(connection):
        character = connection[index]
        if quote:
            if character == quote:
                if index + 1 < len(connection) and connection[index + 1] == quote:
                    index += 1
                else:
                    quote = None
        elif character in ('"', "'"):
            quote = character
        elif character == ";":
            fields.append(connection[start:index])
            start = index + 1
        index += 1
    if quote:
        raise ValueError("В строке подключения БД не закрыты кавычки. Проверьте appsettings.Local.json.")
    fields.append(connection[start:])
    for index, field in enumerate(fields):
        key, separator, value = field.partition("=")
        if separator and key.strip().lower() in ("host", "server"):
            if value.strip().strip("\"'").lower() in ("localhost", "127.0.0.1", "::1", "[::1]"):
                fields[index] = key + "=host.docker.internal"
    return ";".join(fields)


def run(arguments):
    settings = REPOSITORY / "src/Presentation/HospitalInformationSystem.Api/appsettings.Local.json"
    if not settings.is_file():
        raise ValueError("Не найден appsettings.Local.json. Сначала выполните python3 scripts/setup-development.py.")
    try:
        connection = json.loads(settings.read_text(encoding="utf-8"))["ConnectionStrings"]["Postgres"]
    except (ValueError, KeyError, TypeError):
        raise ValueError("В appsettings.Local.json нужна строка ConnectionStrings:Postgres.") from None
    if not isinstance(connection, str) or not connection.strip():
        raise ValueError("В appsettings.Local.json нужна непустая строка ConnectionStrings:Postgres.")
    connection = docker_connection(connection)
    directory = Path(arguments.security_directory).expanduser().resolve()
    if not (directory / "development-security.json").is_file():
        raise ValueError("Не найдены рабочие сертификаты в " + str(directory)
                         + ". Укажите прежний каталог через --security-directory; "
                         "этот скрипт не перевыпускает ключи.")
    spec = importlib.util.spec_from_file_location("regular_application_launcher", REPOSITORY / "scripts/run-secure-development.py")
    launcher = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(launcher)
    environment = os.environ.copy()
    environment["ConnectionStrings__Postgres"] = connection
    if arguments.start_database:
        if not (REPOSITORY / ".env").is_file():
            raise ValueError("Не найден .env для PostgreSQL. Сначала выполните python3 scripts/setup-development.py.")
        result = subprocess.run([
            arguments.docker, "compose", "--project-directory", str(REPOSITORY),
            "--file", str(REPOSITORY / "docker-compose.yml"), "--env-file", str(REPOSITORY / ".env"),
            "up", "-d", "--wait", "postgres"
        ], cwd=REPOSITORY, check=False)
        if result.returncode:
            raise ValueError("Не удалось запустить PostgreSQL. Проверьте Docker Desktop и настройки .env.")
    print("Обычный запуск: рабочая БД и прежние ключи. Администратор не сбрасывается.", flush=True)
    print("После сборки откройте https://localhost:5101", flush=True)
    return launcher.run_application(directory, environment, no_build=arguments.no_build, docker=arguments.docker)


def main():
    parser = argparse.ArgumentParser(description="Обычный запуск API и Web с существующими настройками и ключами.")
    parser.add_argument("--security-directory", default=str(Path.home() / ".local/share/MedSoft/development-security/408944"))
    parser.add_argument("--start-database", action="store_true", help="Также запустить PostgreSQL через Docker Compose.")
    parser.add_argument("--no-build", action="store_true", help="Использовать ранее собранные API-образ и Web.")
    parser.add_argument("--docker", default="docker")
    return run(parser.parse_args())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
