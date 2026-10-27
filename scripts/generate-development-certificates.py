#!/usr/bin/env python3
import argparse
import ipaddress
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parent.parent


def openssl_command(executable, *arguments):
    result = subprocess.run([executable, *map(str, arguments)], capture_output=True,
                            text=True, timeout=120)
    if result.returncode:
        raise ValueError("OpenSSL не выполнил команду " + str(arguments[0]) + ": "
                         + result.stderr.strip())
    return result.stdout


def find_openssl(requested=None):
    candidates = [requested] if requested else [os.environ.get("OPENSSL"), shutil.which("openssl"),
                                               "/opt/homebrew/opt/openssl@3/bin/openssl",
                                               "/usr/local/opt/openssl@3/bin/openssl"]
    for candidate in filter(None, candidates):
        try:
            version = openssl_command(candidate, "version")
        except (OSError, ValueError, subprocess.TimeoutExpired):
            continue
        match = re.match(r"OpenSSL (\d+)\.(\d+)\.(\d+)", version)
        if match and tuple(map(int, match.groups())) >= (1, 1, 1):
            return candidate
    raise ValueError("Нужен OpenSSL >= 1.1.1 с TLS 1.3. Укажите --openssl или OPENSSL; LibreSSL не подходит.")


def private_write(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        output.write(value)


def validate_dns_name(value):
    value = value.lower()
    if len(value) > 253 or not all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", part)
                                    for part in value.split(".")):
        raise ValueError("Некорректное DNS-имя: " + value)
    return value


def require_private_file(path):
    if path.is_symlink() or not path.is_file():
        raise ValueError("Нужен обычный локальный файл: " + str(path))
    if os.name != "nt" and path.stat().st_mode & 0o077:
        raise ValueError("Закрытый файл должен иметь права 0600: " + str(path))


def require_no_symlinks(path):
    for item in [path, *path.parents]:
        if item.is_symlink():
            if sys.platform == "darwin" and str(item) in ("/var", "/tmp") and item.resolve() == Path("/private" + str(item)):
                continue
            raise ValueError("Путь не должен содержать символические ссылки: " + str(path))


def key_pair(executable, destination, password_path=None):
    arguments = ["genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072"]
    if password_path:
        arguments += ["-aes-256-cbc", "-pass", "file:" + str(password_path)]
    openssl_command(executable, *arguments, "-out", destination)
    destination.chmod(0o600)


def certificate_subject(executable, certificate, field):
    value = openssl_command(executable, "x509", "-in", certificate, "-noout", "-" + field,
                            "-nameopt", "sep_multiline,sname,utf8")
    return dict(re.findall(r"^\s*([A-Z][A-Za-z]*)\s*=\s*(.*?)\s*$", value, re.MULTILINE))


def data_protection_certificate(executable, directory, work, organizational_unit):
    directory.mkdir(mode=0o700)
    (directory / "keys").mkdir(mode=0o700)
    password = directory / "wrapping-cert-password.txt"
    private_write(password, secrets.token_urlsafe(36) + "\n")
    key_password = work / "data-protection-key-password.txt"
    private_write(key_password, secrets.token_urlsafe(36) + "\n")
    key = work / "data-protection-key.pem"
    key_pair(executable, key, key_password)
    config = work / "data-protection.cnf"
    private_write(config, "\n".join([
        "[req]", "prompt=no", "distinguished_name=subject", "x509_extensions=extensions",
        "[subject]", "O=ITMO", "OU=" + organizational_unit, "CN=MedSoft Data Protection",
        "[extensions]", "basicConstraints=critical,CA:FALSE", "keyUsage=critical,keyEncipherment",
        "subjectKeyIdentifier=hash", ""]))
    certificate = directory / "wrapping-cert.pem"
    openssl_command(executable, "req", "-new", "-x509", "-sha256", "-days", "3650",
                    "-set_serial", "0x" + secrets.token_hex(16), "-key", key,
                    "-passin", "file:" + str(key_password), "-config", config, "-out", certificate)
    openssl_command(executable, "pkcs12", "-export", "-name", "MedSoft Data Protection", "-inkey", key,
                    "-passin", "file:" + str(key_password), "-in", certificate,
                    "-keypbe", "AES-256-CBC", "-certpbe", "AES-256-CBC", "-macalg", "SHA256",
                    "-iter", "100000", "-out", directory / "wrapping-cert.pfx",
                    "-passout", "file:" + str(password))


def issue_certificates(arguments):
    if not re.fullmatch(r"[1-9][0-9]{2,11}", arguments.isu_id):
        raise ValueError("Укажите действительный числовой ИСУ ID (3–12 цифр), без шаблонов.")
    if not 1 <= arguments.valid_days <= 825:
        raise ValueError("Срок серверного сертификата должен быть от 1 до 825 дней.")
    dns_names = list(dict.fromkeys(["localhost", *(arguments.dns_name or [])]))
    dns_names = [validate_dns_name(name) for name in dns_names]
    addresses = list(dict.fromkeys(["127.0.0.1", "::1", *(arguments.ip_address or [])]))
    try:
        addresses = [str(ipaddress.ip_address(address)) for address in addresses]
    except ValueError as error:
        raise ValueError("Некорректный IP-адрес SAN.") from error
    executable = find_openssl(arguments.openssl)
    destination = Path(arguments.output_directory or
                       Path.home() / ".local/share/MedSoft/development-security" / arguments.isu_id).expanduser().absolute()
    require_no_symlinks(destination)
    destination = destination.resolve()
    if destination == REPOSITORY or REPOSITORY in destination.parents:
        raise ValueError("Сертификаты и ключи необходимо сохранять за пределами репозитория.")
    if destination.exists():
        raise ValueError("Каталог уже существует; файлы не изменены. Для перевыпуска выберите новый каталог.")
    ca_directory = Path(arguments.ca_directory).expanduser().absolute() if arguments.ca_directory else destination / "ca"
    if arguments.ca_directory:
        require_no_symlinks(ca_directory)
    ca_directory = ca_directory.resolve()
    ca_certificate = ca_directory / "ca-cert.pem"
    ca_key = ca_directory / "ca-key.pem"
    ca_password = ca_directory / "ca-key-password.txt"
    organizational_unit = "MedSoft 2026/27 " + arguments.isu_id
    existing_jwt = None
    if arguments.jwt_directory:
        existing_jwt = Path(arguments.jwt_directory).expanduser().absolute()
        require_no_symlinks(existing_jwt)
        existing_jwt = existing_jwt.resolve()
        for filename in ("jwt-signing-private.pem", "jwt-signing-public.pem"):
            require_private_file(existing_jwt / filename)
        if os.name != "nt" and existing_jwt.stat().st_mode & 0o077:
            raise ValueError("Каталог JWT должен иметь права 0700.")
        derived_public = openssl_command(executable, "pkey", "-in", existing_jwt / "jwt-signing-private.pem", "-pubout")
        configured_public = openssl_command(executable, "pkey", "-pubin", "-in", existing_jwt / "jwt-signing-public.pem", "-pubout")
        if derived_public != configured_public:
            raise ValueError("Закрытый и публичный JWT-ключи не образуют одну пару.")
    protection = Path(arguments.data_protection_directory).expanduser().absolute() if arguments.data_protection_directory else destination / "data-protection"
    if arguments.data_protection_directory:
        require_no_symlinks(protection)
        protection = protection.resolve()
        for file in ("wrapping-cert.pfx", "wrapping-cert-password.txt"):
            require_private_file(protection / file)
        if not (protection / "keys").is_dir() or (protection / "keys").is_symlink():
            raise ValueError("Не найден существующий каталог ключей Data Protection.")
        if os.name != "nt" and ((protection.stat().st_mode | (protection / "keys").stat().st_mode) & 0o077):
            raise ValueError("Каталоги Data Protection должны иметь права 0700.")
        openssl_command(executable, "pkcs12", "-in", protection / "wrapping-cert.pfx",
                        "-passin", "file:" + str(protection / "wrapping-cert-password.txt"), "-info", "-noout")
    if arguments.ca_directory:
        require_private_file(ca_key)
        require_private_file(ca_password)
        if ca_certificate.is_symlink() or not ca_certificate.is_file():
            raise ValueError("Не найден публичный сертификат CA.")
        subject = certificate_subject(executable, ca_certificate, "subject")
        issuer = certificate_subject(executable, ca_certificate, "issuer")
        if subject != issuer or subject.get("O") != "ITMO" or subject.get("OU") != organizational_unit:
            raise ValueError("Существующий CA должен быть самоподписанным и содержать нужные O и OU.")
        openssl_command(executable, "verify", "-check_ss_sig", "-CAfile", ca_certificate, ca_certificate)
        openssl_command(executable, "x509", "-in", ca_certificate, "-checkend",
                        str(arguments.valid_days * 86400), "-noout")
    previous_umask = os.umask(0o077)
    created = False
    try:
        destination.mkdir(mode=0o700, parents=True, exist_ok=False)
        created = True
        server = destination / "server"
        server.mkdir(mode=0o700)
        setup = destination / "setup"
        setup.mkdir(mode=0o700)
        setup_token = setup / "token.txt"
        private_write(setup_token, secrets.token_urlsafe(32) + "\n")
        work = destination / ".generation"
        work.mkdir(mode=0o700)
        if not arguments.data_protection_directory:
            data_protection_certificate(executable, protection, work, organizational_unit)
        if not arguments.ca_directory:
            ca_directory.mkdir(mode=0o700)
            private_write(ca_password, secrets.token_urlsafe(36) + "\n")
            key_pair(executable, ca_key, ca_password)
            ca_config = work / "ca.cnf"
            private_write(ca_config, "\n".join([
                "[req]", "prompt=no", "distinguished_name=subject", "x509_extensions=extensions",
                "[subject]", "O=ITMO", "OU=" + organizational_unit, "CN=MedSoft development CA",
                "[extensions]", "basicConstraints=critical,CA:TRUE,pathlen:0",
                "keyUsage=critical,keyCertSign,cRLSign", "subjectKeyIdentifier=hash",
                "authorityKeyIdentifier=keyid:always", ""]))
            openssl_command(executable, "req", "-new", "-x509", "-sha256", "-days", "3650",
                            "-set_serial", "0x" + secrets.token_hex(16), "-key", ca_key,
                            "-passin", "file:" + str(ca_password), "-config", ca_config,
                            "-out", ca_certificate)
        server_password = server / "server-key-password.txt"
        private_write(server_password, secrets.token_urlsafe(36) + "\n")
        server_key = server / "server-key.pem"
        key_pair(executable, server_key, server_password)
        server_config = work / "server.cnf"
        private_write(server_config, "\n".join([
            "[req]", "prompt=no", "distinguished_name=subject", "[subject]", "O=ITMO",
            "OU=" + organizational_unit, "CN=MedSoft local development server",
            "[extensions]", "basicConstraints=critical,CA:FALSE",
            "keyUsage=critical,digitalSignature,keyEncipherment", "extendedKeyUsage=serverAuth",
            "subjectKeyIdentifier=hash", "authorityKeyIdentifier=keyid,issuer", "subjectAltName=@names",
            "[names]", *["DNS." + str(index) + "=" + name for index, name in enumerate(dns_names, 1)],
            *["IP." + str(index) + "=" + address for index, address in enumerate(addresses, 1)], ""]))
        request = work / "server.csr"
        openssl_command(executable, "req", "-new", "-sha256", "-key", server_key,
                        "-passin", "file:" + str(server_password), "-config", server_config, "-out", request)
        server_certificate = server / "server-cert.pem"
        openssl_command(executable, "x509", "-req", "-sha256", "-days", str(arguments.valid_days),
                        "-in", request, "-CA", ca_certificate, "-CAkey", ca_key, "-passin",
                        "file:" + str(ca_password), "-set_serial", "0x" + secrets.token_hex(16),
                        "-extfile", server_config, "-extensions", "extensions", "-out", server_certificate)
        openssl_command(executable, "verify", "-CAfile", ca_certificate, "-purpose", "sslserver",
                        "-verify_hostname", "localhost", server_certificate)
        for address in addresses:
            openssl_command(executable, "verify", "-CAfile", ca_certificate, "-purpose", "sslserver",
                            "-verify_ip", address, server_certificate)
        pfx_password = server / "server-pfx-password.txt"
        private_write(pfx_password, secrets.token_urlsafe(36) + "\n")
        pfx = server / "server.pfx"
        openssl_command(executable, "pkcs12", "-export", "-name", "MedSoft localhost", "-inkey", server_key,
                        "-passin", "file:" + str(server_password), "-in", server_certificate,
                        "-certfile", ca_certificate, "-keypbe", "AES-256-CBC", "-certpbe", "AES-256-CBC",
                        "-macalg", "SHA256", "-iter", "100000", "-out", pfx,
                        "-passout", "file:" + str(pfx_password))
        private_write(server / "server-chain.pem", server_certificate.read_text() + ca_certificate.read_text())
        jwt_private = None
        jwt_public = None
        if existing_jwt:
            jwt_private = existing_jwt / "jwt-signing-private.pem"
            jwt_public = existing_jwt / "jwt-signing-public.pem"
        elif not arguments.skip_jwt:
            jwt_directory = destination / "jwt"
            jwt_directory.mkdir(mode=0o700)
            jwt_private = jwt_directory / "jwt-signing-private.pem"
            jwt_public = jwt_directory / "jwt-signing-public.pem"
            key_pair(executable, jwt_private)
            openssl_command(executable, "pkey", "-in", jwt_private, "-pubout", "-out", jwt_public)
        manifest = {
            "schemaVersion": 1, "isuId": arguments.isu_id, "organization": "ITMO",
            "organizationalUnit": organizational_unit, "dnsNames": dns_names, "ipAddresses": addresses,
            "caCertificatePath": str(ca_certificate), "serverCertificatePath": str(server_certificate),
            "serverPfxPath": str(pfx), "serverPfxPasswordFile": str(pfx_password),
            "setupTokenFile": str(setup_token), "dataProtectionPfxPath": str(protection / "wrapping-cert.pfx"),
            "dataProtectionPfxPasswordFile": str(protection / "wrapping-cert-password.txt"),
            "dataProtectionKeyDirectory": str(protection / "keys"),
            "jwtPrivateKeyPath": str(jwt_private) if jwt_private else None,
            "jwtPublicKeyPath": str(jwt_public) if jwt_public else None}
        private_write(destination / "development-security.json", json.dumps(manifest, indent=2) + "\n")
        shutil.rmtree(work)
        for item in destination.rglob("*"):
            if item.is_file():
                item.chmod(0o600)
        return manifest
    except BaseException:
        if created:
            shutil.rmtree(destination)
        raise
    finally:
        os.umask(previous_umask)


def main():
    parser = argparse.ArgumentParser(description="Создать учебный CA, серверный сертификат и отдельные RSA-ключи JWT.")
    parser.add_argument("--isu-id", required=True, help="Действительный ИСУ ID владельца учебного CA.")
    parser.add_argument("--output-directory", "--output-dir", help="Новый каталог вне репозитория; существующий не перезаписывается.")
    parser.add_argument("--ca-directory", help="Переиспользовать ранее созданный каталог ca при перевыпуске leaf.")
    parser.add_argument("--valid-days", type=int, default=365)
    parser.add_argument("--dns-name", action="append", help="Дополнительное DNS-имя SAN.")
    parser.add_argument("--ip-address", action="append", help="Дополнительный IP SAN.")
    jwt_options = parser.add_mutually_exclusive_group()
    jwt_options.add_argument("--skip-jwt", action="store_true", help="Не создавать JWT-ключ; такой каталог не подходит для launcher API без дополнительной настройки.")
    jwt_options.add_argument("--jwt-directory", help="Сохранить существующую пару JWT-ключей при перевыпуске TLS.")
    parser.add_argument("--data-protection-directory", help="Сохранить существующий каталог Data Protection с PFX и ключами при перевыпуске TLS.")
    parser.add_argument("--openssl", help="Путь к OpenSSL >= 1.1.1.")
    manifest = issue_certificates(parser.parse_args())
    print("Сертификаты и ключи созданы. Доверие в ОС не изменялось.")
    print("Публичный CA: " + manifest["caCertificatePath"])
    print("Серверный PFX: " + manifest["serverPfxPath"])
    print("Пароль PFX находится в отдельном файле: " + manifest["serverPfxPasswordFile"])
    if manifest["jwtPublicKeyPath"]:
        print("Публичный JWT-ключ: " + manifest["jwtPublicKeyPath"])


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
