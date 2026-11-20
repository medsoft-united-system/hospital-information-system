#!/usr/bin/env python3
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys


REPOSITORY = Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location(
    "hl7_client_certificate_helpers", REPOSITORY / "scripts/generate-development-certificates.py")
certificates = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(certificates)


def _command(openssl, *arguments):
    try:
        return certificates.openssl_command(openssl, *arguments)
    except ValueError:
        raise ValueError("OpenSSL не смог выпустить или проверить клиентский сертификат.") from None


def _fingerprint(openssl, path):
    value = _command(openssl, "x509", "-in", path, "-noout", "-fingerprint", "-sha256")
    fingerprint = value.partition("=")[2].replace(":", "").strip().lower()
    if not re.fullmatch(r"[a-f0-9]{64}", fingerprint):
        raise ValueError("Не удалось определить SHA-256 сертификата CA.")
    return fingerprint


def _public_ca(openssl, path):
    certificates.require_no_symlinks(path)
    if not path.is_file():
        raise ValueError("Не найден публичный сертификат CA.")
    value = path.read_text(encoding="ascii")
    if value.count("-----BEGIN CERTIFICATE-----") != 1 or "PRIVATE KEY" in value:
        raise ValueError("Нужен один публичный сертификат CA без закрытого ключа.")
    text = _command(openssl, "x509", "-in", path, "-noout", "-text")
    if "CA:TRUE" not in text or "Certificate Sign" not in text:
        raise ValueError("Сертификат должен быть CA с правом подписи сертификатов.")
    _command(openssl, "verify", "-check_ss_sig", "-CAfile", path, path)
    _command(openssl, "x509", "-in", path, "-checkend", "60", "-noout")
    return _command(openssl, "x509", "-in", path, "-outform", "PEM")


def _validate_existing(directory, ca_path, openssl):
    manifest_path = directory / "client-security.json"
    certificates.require_private_file(manifest_path)
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (ValueError, UnicodeError):
        raise ValueError("Некорректный client-security.json; существующие файлы не изменены.") from None
    expected = {
        "caCertificatePath": str(directory / "ca-cert.pem"),
        "clientCertificatePath": str(directory / "client-cert.pem"),
        "clientKeyPath": str(directory / "client-key.pem")}
    if not isinstance(manifest, dict) or type(manifest.get("schemaVersion")) is not int or \
            manifest["schemaVersion"] != 1 or any(manifest.get(key) != path for key, path in expected.items()):
        raise ValueError("Каталог содержит другой клиентский профиль; файлы не изменены.")
    for path in expected.values():
        certificates.require_no_symlinks(Path(path))
        certificates.require_private_file(Path(path))
    issuer_fingerprint = _fingerprint(openssl, ca_path)
    if manifest.get("issuerCaSha256") != issuer_fingerprint or \
            _fingerprint(openssl, directory / "ca-cert.pem") != issuer_fingerprint:
        raise ValueError("Клиентский выпуск относится к другому CA; выберите новый каталог.")
    certificate = directory / "client-cert.pem"
    _command(openssl, "verify", "-CAfile", ca_path, "-purpose", "sslclient", certificate)
    _command(openssl, "x509", "-in", certificate, "-checkend", "60", "-noout")
    text = _command(openssl, "x509", "-in", certificate, "-noout", "-text")
    if "CA:FALSE" not in text or "TLS Web Client Authentication" not in text or \
            "TLS Web Server Authentication" in text or "Digital Signature" not in text:
        raise ValueError("Требуется leaf-сертификат только с Client Authentication EKU.")
    key = directory / "client-key.pem"
    if "ENCRYPTED" in key.read_text(encoding="ascii"):
        raise ValueError("Для desktop нужен PEM-ключ без пароля, защищённый правами файла 0600.")
    private_public = _command(openssl, "pkey", "-in", key, "-pubout")
    certificate_public = _command(openssl, "x509", "-in", certificate, "-noout", "-pubkey")
    if private_public != certificate_public:
        raise ValueError("Клиентский сертификат и закрытый ключ не образуют пару.")
    return manifest


def prepare_client(ca_directory, output_directory, openssl=None):
    executable = certificates.find_openssl(openssl)
    source = Path(ca_directory).expanduser().absolute()
    destination = Path(output_directory).expanduser().absolute()
    for path in (source, destination):
        certificates.require_no_symlinks(path)
    source, destination = source.resolve(), destination.resolve()
    if destination == REPOSITORY or REPOSITORY in destination.parents or \
            destination == source or source in destination.parents:
        raise ValueError("Клиентский выпуск должен находиться вне репозитория и каталога CA.")
    ca = source / "ca-cert.pem"
    public_ca = _public_ca(executable, ca)
    if destination.exists():
        if not destination.is_dir() or (os.name != "nt" and destination.stat().st_mode & 0o077):
            raise ValueError("Каталог клиентского выпуска должен иметь права 0700.")
        return _validate_existing(destination, ca, executable)
    ca_key, ca_password = source / "ca-key.pem", source / "ca-key-password.txt"
    for path in (ca_key, ca_password):
        certificates.require_no_symlinks(path)
        certificates.require_private_file(path)
    _command(executable, "x509", "-in", ca, "-checkend", str(365 * 86400), "-noout")
    if _command(executable, "pkey", "-in", ca_key, "-passin", "file:" + str(ca_password), "-pubout") != \
            _command(executable, "x509", "-in", ca, "-noout", "-pubkey"):
        raise ValueError("Закрытый ключ CA не соответствует сертификату CA.")
    previous_umask = os.umask(0o077)
    created = False
    try:
        destination.mkdir(mode=0o700, parents=True, exist_ok=False)
        created = True
        work = destination / ".generation"
        work.mkdir(mode=0o700)
        key = destination / "client-key.pem"
        certificates.key_pair(executable, key)
        config = work / "client.cnf"
        certificates.private_write(config, "\n".join([
            "[req]", "prompt=no", "distinguished_name=subject", "[subject]",
            "CN=MedSoft HL7 Import", "[extensions]", "basicConstraints=critical,CA:FALSE",
            "keyUsage=critical,digitalSignature", "extendedKeyUsage=clientAuth",
            "subjectKeyIdentifier=hash", "authorityKeyIdentifier=keyid,issuer", ""]))
        request = work / "client.csr"
        _command(executable, "req", "-new", "-sha256", "-key", key, "-config", config, "-out", request)
        certificate = destination / "client-cert.pem"
        _command(executable, "x509", "-req", "-sha256", "-days", "365", "-in", request,
                 "-CA", ca, "-CAkey", ca_key, "-passin", "file:" + str(ca_password),
                 "-set_serial", "0x" + secrets.token_hex(16), "-extfile", config,
                 "-extensions", "extensions", "-out", certificate)
        certificate.chmod(0o600)
        certificates.private_write(destination / "ca-cert.pem", public_ca)
        manifest = {
            "schemaVersion": 1, "caCertificatePath": str(destination / "ca-cert.pem"),
            "clientCertificatePath": str(certificate), "clientKeyPath": str(key),
            "issuerCaSha256": _fingerprint(executable, ca)}
        certificates.private_write(destination / "client-security.json", json.dumps(manifest, indent=2) + "\n")
        shutil.rmtree(work)
        return _validate_existing(destination, ca, executable)
    except BaseException:
        if created:
            shutil.rmtree(destination)
        raise
    finally:
        os.umask(previous_umask)


def main():
    parser = argparse.ArgumentParser(description="Подготовить учебный ClientAuth PEM-выпуск для HL7 desktop без замены ключей HIS.")
    parser.add_argument("--ca-directory", required=True)
    parser.add_argument("--output-directory", required=True)
    parser.add_argument("--openssl")
    args = parser.parse_args()
    manifest = prepare_client(args.ca_directory, args.output_directory, args.openssl)
    print("Клиентский сертификат: " + manifest["clientCertificatePath"])
    print("Закрытый ключ desktop (0600): " + manifest["clientKeyPath"])
    print("Публичный CA: " + manifest["caCertificatePath"])
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, UnicodeError, subprocess.SubprocessError):
        print("Не удалось подготовить клиентский сертификат: проверьте CA, пути, права и срок действия. Существующий выпуск не перезаписывается.", file=sys.stderr)
        sys.exit(1)
