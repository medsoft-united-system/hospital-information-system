#!/usr/bin/env python3
import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parent.parent
PRESERVED_PATHS = (
    "caCertificatePath", "jwtPrivateKeyPath", "jwtPublicKeyPath", "dataProtectionPfxPath",
    "dataProtectionPfxPasswordFile", "dataProtectionKeyDirectory")


def load_tool(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPOSITORY / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_manifest(directory, certificates):
    certificates.require_no_symlinks(directory)
    if not directory.is_dir() or (os.name != "nt" and directory.stat().st_mode & 0o077):
        raise ValueError("Нужен существующий закрытый каталог сертификатов с правами 0700.")
    path = directory / "development-security.json"
    certificates.require_private_file(path)
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except ValueError:
        raise ValueError("Некорректный development-security.json.") from None
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1 or not isinstance(
            manifest.get("isuId"), str) or not re.fullmatch(r"[1-9][0-9]{2,11}", manifest["isuId"]):
        raise ValueError("Неподдерживаемый манифест сертификатов или ИСУ ID.")
    for field in (*PRESERVED_PATHS, "setupTokenFile", "serverCertificatePath", "serverPfxPath", "serverPfxPasswordFile"):
        value = manifest.get(field)
        if not isinstance(value, str) or not Path(value).is_absolute():
            raise ValueError("В манифесте отсутствует абсолютный путь " + field + ".")
        certificates.require_no_symlinks(Path(value))
    for field in ("jwtPrivateKeyPath", "jwtPublicKeyPath", "dataProtectionPfxPath",
                  "dataProtectionPfxPasswordFile", "setupTokenFile", "serverPfxPath", "serverPfxPasswordFile"):
        certificates.require_private_file(Path(manifest[field]))
    for field in ("caCertificatePath", "serverCertificatePath"):
        if not Path(manifest[field]).is_file():
            raise ValueError("Не найден публичный сертификат " + field + ".")
    protection = Path(manifest["dataProtectionKeyDirectory"])
    if not protection.is_dir() or (os.name != "nt" and protection.stat().st_mode & 0o077):
        raise ValueError("Не найден закрытый keyring Data Protection.")
    return manifest


def validate_profile(directory, manifest, source, certificates, openssl):
    if manifest["isuId"] != source["isuId"] or any(
            Path(manifest[field]).resolve() != Path(source[field]).resolve() for field in PRESERVED_PATHS):
        raise ValueError("Каталог интеграции относится к другим исходным ключам; выберите новый --output-directory.")
    for field in ("serverCertificatePath", "serverPfxPath", "serverPfxPasswordFile", "setupTokenFile"):
        if not Path(manifest[field]).resolve().is_relative_to(directory):
            raise ValueError("TLS и копия owner token должны находиться внутри каталога интеграции.")
    if Path(manifest["setupTokenFile"]).read_bytes() != Path(source["setupTokenFile"]).read_bytes():
        raise ValueError("Owner token каталога интеграции не совпадает с исходным; файлы не изменены.")
    for name in ("localhost", "his-api"):
        certificates.openssl_command(openssl, "verify", "-CAfile", manifest["caCertificatePath"],
                                     "-purpose", "sslserver", "-verify_hostname", name,
                                     manifest["serverCertificatePath"])
    certificates.openssl_command(openssl, "x509", "-in", manifest["serverCertificatePath"],
                                 "-checkend", "60", "-noout")


def export_lis_ca(directory, manifest, certificates, openssl):
    path = directory / "lis-ca-cert.pem"
    certificates.require_no_symlinks(path)
    certificate = certificates.openssl_command(openssl, "x509", "-in", manifest["caCertificatePath"],
                                                "-outform", "PEM").encode("ascii")
    if path.exists():
        if not path.is_file() or path.read_bytes() != certificate:
            raise ValueError("Публичный CA для LIS не совпадает с манифестом интеграции.")
    else:
        certificates.private_write_bytes(path, certificate)
    if os.name != "nt" and path.stat().st_mode & 0o777 != 0o644:
        path.chmod(0o644)
    return path


def prepare_security(arguments):
    certificates = load_tool("lab3_certificates", "generate-development-certificates.py")
    source_directory = Path(arguments.security_directory).expanduser().absolute()
    certificates.require_no_symlinks(source_directory)
    source_directory = source_directory.resolve()
    source = load_manifest(source_directory, certificates)
    output = Path(arguments.output_directory).expanduser().absolute() if arguments.output_directory else \
        source_directory.with_name(source_directory.name + "-lab3")
    certificates.require_no_symlinks(output)
    output = output.resolve()
    if output == REPOSITORY or REPOSITORY in output.parents or output == source_directory or source_directory in output.parents:
        raise ValueError("Каталог интеграции должен быть новым отдельным каталогом вне репозитория и исходного выпуска.")
    openssl = certificates.find_openssl(arguments.openssl)
    if output.exists():
        manifest = load_manifest(output, certificates)
    else:
        ca = Path(source["caCertificatePath"])
        jwt = Path(source["jwtPrivateKeyPath"])
        protection = Path(source["dataProtectionPfxPath"])
        if ca.name != "ca-cert.pem" or jwt.name != "jwt-signing-private.pem" or \
                Path(source["jwtPublicKeyPath"]) != jwt.parent / "jwt-signing-public.pem" or \
                protection.name != "wrapping-cert.pfx" or \
                Path(source["dataProtectionPfxPasswordFile"]) != protection.parent / "wrapping-cert-password.txt" or \
                Path(source["dataProtectionKeyDirectory"]) != protection.parent / "keys":
            raise ValueError("Исходные файлы должны соответствовать структуре генератора MedSoft.")
        manifest = certificates.issue_certificates(argparse.Namespace(
            isu_id=source["isuId"], output_directory=str(output), ca_directory=str(ca.parent),
            jwt_directory=str(jwt.parent), data_protection_directory=str(protection.parent),
            setup_token_file=source["setupTokenFile"], valid_days=365, dns_name=["his-api"],
            ip_address=[], openssl=openssl, skip_jwt=False))
    validate_profile(output, manifest, source, certificates, openssl)
    return output, export_lis_ca(output, manifest, certificates, openssl)


def database_environment():
    environment = os.environ.copy()
    connection = environment.get("ConnectionStrings__Postgres", "").strip()
    if not connection:
        if sys.platform.startswith("linux"):
            raise ValueError("На Linux задайте ConnectionStrings__Postgres с БД, доступной из контейнера API; "
                             "loopback-порт хоста не становится доступным через Docker DNS.")
        settings = REPOSITORY / "src/Presentation/HospitalInformationSystem.Api/appsettings.Local.json"
        try:
            connection = json.loads(settings.read_text(encoding="utf-8"))["ConnectionStrings"]["Postgres"]
        except (OSError, ValueError, KeyError, TypeError):
            raise ValueError("Настройте appsettings.Local.json или ConnectionStrings__Postgres для рабочей БД.") from None
    if not isinstance(connection, str) or not connection.strip():
        raise ValueError("Нужна непустая строка подключения PostgreSQL.")
    regular = load_tool("lab3_database_connection", "run-application.py")
    environment["ConnectionStrings__Postgres"] = regular.docker_connection(connection)
    environment["Fhir__PublicBaseUrl"] = "https://his-api:5201/fhir"
    environment["AllowedHosts"] = "localhost;127.0.0.1;[::1];his-api"
    return environment


def run(arguments):
    launcher = load_tool("lab3_secure_launcher", "run-secure-development.py")
    launcher.validate_network(arguments.docker_network, "his-api")
    directory, public_ca = prepare_security(arguments)
    print("Каталог HTTPS интеграции: " + str(directory), flush=True)
    print("Публичный CA для LIS: " + str(public_ca), flush=True)
    print("Адрес HIS из Docker-сети " + arguments.docker_network + ": https://his-api:5201", flush=True)
    print("JWT, Data Protection, owner token и рабочая БД сохраняются. Доверие ОС не изменяется.", flush=True)
    if not arguments.run:
        return 0
    environment = database_environment()
    if arguments.start_database:
        if not (REPOSITORY / ".env").is_file():
            raise ValueError("Не найден .env рабочей PostgreSQL; выполните первоначальную настройку.")
        result = subprocess.run([
            arguments.docker, "compose", "--project-directory", str(REPOSITORY),
            "--file", str(REPOSITORY / "docker-compose.yml"), "--env-file", str(REPOSITORY / ".env"),
            "up", "-d", "--wait", "postgres"], cwd=REPOSITORY, check=False)
        if result.returncode:
            raise ValueError("Не удалось запустить рабочую PostgreSQL; приложение не запущено.")
    return launcher.run_application(directory, environment, no_build=arguments.no_build, docker=arguments.docker,
                                    docker_network=arguments.docker_network, network_alias="his-api")


def main():
    parser = argparse.ArgumentParser(description="Подготовить HTTPS HIS для LIS без сброса БД, администратора и ключей.")
    parser.add_argument("--security-directory", default=str(Path.home() / ".local/share/MedSoft/development-security/408944"))
    parser.add_argument("--output-directory", help="Отдельный каталог; по умолчанию рядом с исходным, с суффиксом -lab3.")
    parser.add_argument("--openssl", help="Путь к OpenSSL >= 1.1.1.")
    parser.add_argument("--run", action="store_true", help="После подготовки запустить рабочие API и Web.")
    parser.add_argument("--start-database", action="store_true", help="Перед запуском поднять рабочую PostgreSQL без удаления данных.")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--docker", default="docker")
    parser.add_argument("--docker-network", default="medsoft-lab3")
    return run(parser.parse_args())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
