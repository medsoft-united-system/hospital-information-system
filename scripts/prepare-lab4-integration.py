#!/usr/bin/env python3
import argparse
import importlib.util
from pathlib import Path
import subprocess
import sys


REPOSITORY = Path(__file__).resolve().parent.parent
DEFAULT_SECURITY = Path.home() / ".local/share/MedSoft/development-security/408944"


def load_tool(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPOSITORY / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_port(value):
    if isinstance(value, bool) or not str(value).isascii() or not str(value).isdecimal():
        raise ValueError("MLLP-порт должен быть целым числом от 1 до 65535.")
    port = int(value)
    if not 1 <= port <= 65535 or port in (5101, 5201):
        raise ValueError("MLLP-порт должен быть от 1 до 65535 и отличаться от HTTPS-портов 5101/5201.")
    return port


def validate_client_ca(path, certificates, openssl):
    path = Path(path).expanduser().absolute()
    certificates.require_no_symlinks(path)
    if not path.is_file() or "," in str(path):
        raise ValueError("Укажите обычный файл публичного client CA без запятых в пути.")
    raw = path.read_bytes()
    if b"PRIVATE KEY" in raw or raw.count(b"-----BEGIN CERTIFICATE-----") != 1:
        raise ValueError("Для client trust нужен один публичный CA-сертификат без private key.")
    description = certificates.openssl_command(openssl, "x509", "-in", path, "-noout", "-text")
    if "CA:TRUE" not in description or "Certificate Sign" not in description:
        raise ValueError("Client trust требует CA-сертификат с KeyCertSign.")
    certificates.openssl_command(openssl, "verify", "-check_ss_sig", "-CAfile", path, path)
    certificates.openssl_command(openssl, "x509", "-in", path, "-checkend", "60", "-noout")
    return path.resolve()


def prepare_security(arguments):
    validate_port(arguments.mllp_port)
    lab3 = load_tool("lab4_preserved_https", "prepare-lab3-integration.py")
    launcher = load_tool("lab4_network_validation", "run-secure-development.py")
    launcher.validate_network(arguments.docker_network, "his-api")
    certificates = load_tool("lab4_certificate_helpers", "generate-development-certificates.py")
    openssl = certificates.find_openssl(arguments.openssl)
    external_ca = validate_client_ca(arguments.client_ca, certificates, openssl) if arguments.client_ca else None
    https_directory, _ = lab3.prepare_security(argparse.Namespace(
        security_directory=arguments.security_directory,
        output_directory=arguments.integration_security_directory,
        openssl=openssl))
    https_manifest = lab3.load_manifest(https_directory, certificates)
    if external_ca:
        client = {"caCertificatePath": str(external_ca)}
    else:
        source_directory = Path(arguments.security_directory).expanduser().resolve()
        source = lab3.load_manifest(source_directory, certificates)
        client_directory = Path(arguments.output_directory).expanduser().absolute() if arguments.output_directory else \
            source_directory.with_name(source_directory.name + "-lab4")
        issuer = load_tool("lab4_offline_client_certificate", "prepare-hl7-client-certificate.py")
        client = issuer.prepare_client(Path(source["caCertificatePath"]).parent, client_directory, openssl=openssl)
    return {
        "httpsDirectory": str(https_directory),
        "serverCaCertificatePath": https_manifest["caCertificatePath"],
        "serverPfxPath": https_manifest["serverPfxPath"],
        "serverPfxPasswordFile": https_manifest["serverPfxPasswordFile"],
        "clientCaCertificatePath": client["caCertificatePath"],
        "clientCertificatePath": client.get("clientCertificatePath"),
        "clientKeyPath": client.get("clientKeyPath"),
    }


def application_environment(arguments, profile, *, base_environment=None):
    lab3 = load_tool("lab4_preserved_database", "prepare-lab3-integration.py")
    environment = lab3.database_environment() if base_environment is None else dict(base_environment)
    environment.update({
        "Hl7Import__Enabled": "true",
        "Hl7Import__ListenAddress": "0.0.0.0",
        "Hl7Import__Port": str(validate_port(arguments.mllp_port)),
        "Hl7Import__ServerCertificate__PfxPath": profile["serverPfxPath"],
        "Hl7Import__ServerCertificate__PasswordFile": profile["serverPfxPasswordFile"],
        "Hl7Import__ClientTrust__CaCertificatePath": profile["clientCaCertificatePath"],
        "Database__ApplyMigrations": "true",
        "Database__Seed": "false",
    })
    return environment


def run(arguments):
    profile = prepare_security(arguments)
    print("HTTPS для desktop: https://localhost:5201", flush=True)
    print("Интерфейс HIS: https://localhost:5101", flush=True)
    print("MLLP: localhost:" + str(validate_port(arguments.mllp_port)) + " (TLS 1.3, обязательный client certificate).", flush=True)
    print("HTTPS-профиль: " + profile["httpsDirectory"], flush=True)
    print("Публичный CA HTTPS/MLLP сервера: " + profile["serverCaCertificatePath"], flush=True)
    print("Доверенный client CA: " + profile["clientCaCertificatePath"], flush=True)
    if profile["clientCertificatePath"]:
        print("Учебный client certificate: " + profile["clientCertificatePath"], flush=True)
        print("Учебный client private key: " + profile["clientKeyPath"], flush=True)
    else:
        print("Используйте клиентский выпуск Т7, подписанный указанным client CA.", flush=True)
    print("API key создайте в HIS: Сервисные аккаунты → роль HL7 Import. Ключ не хранится в сертификате.", flush=True)
    print("JWT, Data Protection, администратор, БД и подключение LIS сохраняются. Доверие ОС не изменяется.", flush=True)
    if not arguments.run:
        return 0
    environment = application_environment(arguments, profile)
    if arguments.start_database:
        if not (REPOSITORY / ".env").is_file():
            raise ValueError("Не найден .env рабочей PostgreSQL; сначала выполните make prepare.")
        result = subprocess.run([
            arguments.docker, "compose", "--project-directory", str(REPOSITORY),
            "--file", str(REPOSITORY / "docker-compose.yml"), "--env-file", str(REPOSITORY / ".env"),
            "up", "-d", "--wait", "postgres"], cwd=REPOSITORY, check=False)
        if result.returncode:
            raise ValueError("Не удалось запустить рабочую PostgreSQL; API не запущен.")
    launcher = load_tool("lab4_secure_launcher", "run-secure-development.py")
    return launcher.run_application(
        profile["httpsDirectory"], environment, no_build=arguments.no_build,
        docker=arguments.docker, dotnet=arguments.dotnet,
        docker_network=arguments.docker_network, network_alias="his-api")


def main():
    parser = argparse.ArgumentParser(description="Подготовить и запустить HIS с MLLP/mTLS, сохранив данные лабораторной 3.")
    parser.add_argument("--security-directory", default=str(DEFAULT_SECURITY))
    parser.add_argument("--integration-security-directory", help="Прежний HTTPS-каталог Lab3; по умолчанию SECURITY_DIR-lab3.")
    parser.add_argument("--output-directory", help="Каталог учебного client certificate вне репозитория; по умолчанию SECURITY_DIR-lab4.")
    parser.add_argument("--client-ca", help="Публичный CA выпуска Т7. При задании учебный клиентский ключ не генерируется.")
    parser.add_argument("--mllp-port", default="2575", type=validate_port)
    parser.add_argument("--openssl")
    parser.add_argument("--docker-network", default="medsoft-lab3")
    parser.add_argument("--docker", default="docker")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--run", action="store_true")
    parser.add_argument("--start-database", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    return run(parser.parse_args())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
