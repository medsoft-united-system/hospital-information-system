#!/usr/bin/env python3
import argparse
import importlib.util
import json
import os
import re
import secrets
import shlex
import subprocess
import sys
import time
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parent.parent


def load_launcher():
    spec = importlib.util.spec_from_file_location("secure_demo_launcher", REPOSITORY / "scripts/run-secure-development.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def docker_command(docker, *arguments, environment=None):
    result = subprocess.run([docker, *arguments], env=environment, capture_output=True,
                            text=True, timeout=120, check=False)
    if result.returncode:
        raise ValueError("Ошибка Docker (" + arguments[0] + "). Проверьте Docker Desktop; "
                         "для загрузки postgres:17-alpine нужен доступ к registry.")
    return result.stdout.strip()


def prepare_security(arguments):
    directory = Path.home() / ".local/share/MedSoft/demo-security" / arguments.isu_id
    if arguments.new_certificates:
        directory = directory.parent / (arguments.isu_id + "-" + secrets.token_hex(6))
    manifest_path = directory / "development-security.json"
    if not manifest_path.is_file():
        result = subprocess.run([
            sys.executable, str(REPOSITORY / "scripts/generate-development-certificates.py"),
            "--isu-id", arguments.isu_id, "--output-dir", str(directory)
        ], check=False)
        if result.returncode:
            raise ValueError("Не удалось подготовить сертификаты демонстрационного стенда.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    launcher = load_launcher()
    ca = directory / "ca/ca-cert.pem"
    if not ca.is_file():
        raise ValueError("Не найден публичный CA демонстрационного стенда.")
    trust = ["security", "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k",
             str(Path.home() / "Library/Keychains/login.keychain-db"), str(ca)]
    if arguments.trust_ca:
        subprocess.run(trust, check=True)
    elif sys.platform == "darwin":
        print("Если CA ещё не доверен, выполните в другом терминале:\n" + shlex.join(trust), flush=True)
    print("Для копирования токена владельца выполните в другом терминале:\n"
          + "pbcopy < " + shlex.quote(manifest["setupTokenFile"]), flush=True)
    return directory, launcher


def run(arguments):
    if not re.fullmatch(r"[0-9]{1,16}", arguments.isu_id):
        raise ValueError("ИСУ должен содержать от 1 до 16 цифр.")
    if arguments.trust_ca and sys.platform != "darwin":
        raise ValueError("--trust-ca поддерживается только на macOS; на другой ОС установите доверие CA вручную.")
    docker_command(arguments.docker, "info")
    security_directory, launcher = prepare_security(arguments)
    name = "medsoft-setup-demo-" + secrets.token_hex(8)
    environment = os.environ.copy()
    environment["POSTGRES_PASSWORD"] = secrets.token_urlsafe(32)
    environment["POSTGRES_USER"] = "postgres"
    environment["POSTGRES_DB"] = "hospital"
    print("Создаётся отдельная пустая БД. После Ctrl+C она будет удалена. Рабочая БД не используется.", flush=True)
    try:
        docker_command(arguments.docker, "run", "--detach", "--rm", "--name", name,
                       "--publish", "127.0.0.1::5432", "--tmpfs", "/var/lib/postgresql/data:rw",
                       "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_USER", "--env", "POSTGRES_DB",
                       "postgres:17-alpine", environment=environment)
        deadline = time.monotonic() + 90
        while True:
            result = subprocess.run([arguments.docker, "exec", name, "pg_isready", "-h", "127.0.0.1",
                                     "-U", "postgres", "-d", "hospital"],
                                    capture_output=True, timeout=10, check=False)
            if result.returncode == 0:
                break
            if time.monotonic() >= deadline:
                raise ValueError("Демонстрационная PostgreSQL не запустилась за 90 секунд.")
            time.sleep(1)
        binding = docker_command(arguments.docker, "port", name, "5432/tcp")
        match = re.fullmatch(r"127\.0\.0\.1:([0-9]{1,5})", binding)
        if not match or not 1 <= int(match[1]) <= 65535:
            raise ValueError("Docker вернул неожиданный порт демонстрационной БД.")
        application_environment = os.environ.copy()
        application_environment["ConnectionStrings__Postgres"] = (
            "Host=host.docker.internal;Port=" + match[1]
            + ";Database=hospital;Username=postgres;Password=" + environment["POSTGRES_PASSWORD"])
        application_environment["Database__ApplyMigrations"] = "true"
        application_environment["Database__Seed"] = "false"
        application_environment["Authentication__Setup__AdministratorUserName"] = "admin"
        print("Откройте https://localhost:5101/admin/login после сборки. "
              "Создайте пароль администратора и настройте TOTP. Логин: admin.", flush=True)
        return launcher.run_application(security_directory, application_environment,
                                        no_build=arguments.no_build, docker=arguments.docker)
    finally:
        try:
            docker_command(arguments.docker, "rm", "--force", name)
            print("Временная демонстрационная БД удалена. Сертификаты сохранены для следующего показа.", flush=True)
        except (OSError, ValueError, subprocess.TimeoutExpired):
            print("Не удалось удалить временный контейнер. Удалите только его:\n"
                  + shlex.join([arguments.docker, "rm", "--force", name]), file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description="Показ первоначальной настройки на отдельной одноразовой БД.")
    parser.add_argument("--isu-id", default="408944", help="ИСУ для учебного CA (по умолчанию 408944).")
    parser.add_argument("--new-certificates", action="store_true",
                        help="Создать новый отдельный выпуск сертификатов, ключей и owner token.")
    parser.add_argument("--trust-ca", action="store_true",
                        help="Добавить публичный CA стенда в пользовательскую связку ключей macOS.")
    parser.add_argument("--no-build", action="store_true", help="Использовать уже собранные API-образ и Web.")
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
