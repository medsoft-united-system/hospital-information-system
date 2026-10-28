#!/usr/bin/env python3
import argparse
import json
import os
import secrets
import signal
import subprocess
import sys
import time
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parent.parent
PROJECTS = {
    "api": REPOSITORY / "src/Presentation/HospitalInformationSystem.Api",
    "web": REPOSITORY / "src/Presentation/HospitalInformationSystem.Web"}
CONTAINER_IMAGE = "medsoft-api:development"


def certificate_environment(directory, environment=None):
    directory = Path(directory).expanduser().resolve()
    manifest = json.loads((directory / "development-security.json").read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 1:
        raise ValueError("Неподдерживаемая версия development-security.json.")
    paths = []
    for field in ("serverPfxPath", "serverPfxPasswordFile"):
        raw_path = Path(manifest[field])
        path = raw_path.resolve()
        if not path.is_relative_to(directory) or raw_path.is_symlink() or not path.is_file():
            raise ValueError("Файл сертификата или пароля должен находиться внутри каталога стенда.")
        if os.name != "nt" and path.stat().st_mode & 0o077:
            raise ValueError("Сертификат и пароль должны иметь права 0600.")
        paths.append(path)
    password = paths[1].read_text(encoding="utf-8").rstrip("\r\n")
    if not password or any(character in password for character in "\r\n\0"):
        raise ValueError("Файл должен содержать один непустой пароль PFX.")
    environment = dict(os.environ if environment is None else environment)
    environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = str(paths[0])
    environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = password
    return environment


def stop_process(process):
    if process.poll() is not None:
        return
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGTERM)
        else:
            process.terminate()
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
        process.wait(timeout=5)
    except ProcessLookupError:
        pass


def api_configuration(directory, host_environment, name, native=False):
    directory = Path(directory).expanduser().resolve()
    manifest = json.loads((directory / "development-security.json").read_text(encoding="utf-8"))
    if not native and not host_environment.get("ConnectionStrings__Postgres", "").strip():
        raise ValueError("Для API в Docker задайте ConnectionStrings__Postgres в окружении. "
                         "Для PostgreSQL на Mac используйте Host=host.docker.internal.")
    environment = host_environment.copy()
    environment["ASPNETCORE_ENVIRONMENT"] = "Development"
    environment["ASPNETCORE_URLS"] = "https://localhost:5201" if native else "https://0.0.0.0:5201"
    bindings = [
        ("serverPfxPath", "/run/secrets/server.pfx", "ASPNETCORE_Kestrel__Certificates__Default__Path"),
        ("jwtPrivateKeyPath", "/run/secrets/jwt-private.pem", "Authentication__Jwt__PrivateKeyPath"),
        ("jwtPublicKeyPath", "/run/secrets/jwt-public.pem", "Authentication__Jwt__PublicKeyPath"),
        ("setupTokenFile", "/run/secrets/setup-token.txt", "Authentication__Setup__TokenFile")]
    options = ["run", "--rm", "--init", "--name", name, "--publish", "127.0.0.1:5201:5201"]
    if os.name == "posix":
        options += ["--user", str(os.getuid()) + ":" + str(os.getgid())]
    for field, target, variable in bindings:
        if not manifest.get(field):
            if field.startswith("jwt"):
                raise ValueError("Для запуска API используйте каталог первоначального выпуска с отдельными JWT-ключами.")
            raise ValueError("В манифесте отсутствует " + field + "; выполните новый выпуск сертификатов.")
        raw_path = Path(manifest[field])
        path = raw_path.resolve()
        reusable_jwt = field.startswith("jwt") and path.parent == Path(manifest["jwtPrivateKeyPath"]).resolve().parent \
            and path.parent == Path(manifest["jwtPublicKeyPath"]).resolve().parent \
            and path.name == ("jwt-signing-private.pem" if field == "jwtPrivateKeyPath" else "jwt-signing-public.pem")
        if (not path.is_relative_to(directory) and not reusable_jwt) or raw_path.is_symlink() or not path.is_file():
            raise ValueError("Файлы секретов должны находиться в каталоге стенда.")
        if reusable_jwt and os.name != "nt" and path.parent.stat().st_mode & 0o077:
            raise ValueError("Каталог JWT должен иметь права 0700.")
        if os.name != "nt" and path.stat().st_mode & 0o077:
            raise ValueError("Файлы секретов должны иметь права 0600.")
        if "," in str(path):
            raise ValueError("Путь каталога стенда не должен содержать запятые для Docker bind mount.")
        options += ["--mount", "type=bind,src=" + str(path) + ",dst=" + target + ",readonly"]
        environment[variable] = str(path) if native else target
    protection_pfx = Path(manifest["dataProtectionPfxPath"])
    protection_password = Path(manifest["dataProtectionPfxPasswordFile"])
    protection = Path(manifest["dataProtectionKeyDirectory"])
    protection_directory = protection_pfx.parent.resolve()
    if protection_pfx.name != "wrapping-cert.pfx" or protection_password.name != "wrapping-cert-password.txt" \
            or protection.name != "keys" or protection_password.parent.resolve() != protection_directory \
            or protection.parent.resolve() != protection_directory:
        raise ValueError("Сертификат, пароль и keyring должны относиться к одному каталогу Data Protection.")
    for path in (protection_pfx, protection_password):
        if path.is_symlink() or not path.is_file() or (os.name != "nt" and path.stat().st_mode & 0o077):
            raise ValueError("PFX и пароль Data Protection должны быть обычными файлами с правами 0600.")
        if "," in str(path):
            raise ValueError("Путь каталога стенда не должен содержать запятые для Docker bind mount.")
    if protection.is_symlink() or not protection.is_dir():
        raise ValueError("Не найден локальный каталог data-protection.")
    if os.name != "nt" and (protection.stat().st_mode | protection_directory.stat().st_mode) & 0o077:
        raise ValueError("Каталог data-protection должен иметь права 0700.")
    if "," in str(protection):
        raise ValueError("Путь каталога стенда не должен содержать запятые для Docker bind mount.")
    options += ["--mount", "type=bind,src=" + str(protection) + ",dst=/var/lib/medsoft/data-protection"]
    options += ["--mount", "type=bind,src=" + str(protection_pfx) + ",dst=/run/secrets/data-protection.pfx,readonly"]
    wrapping_password = protection_password.read_text(encoding="utf-8").rstrip("\r\n")
    if not wrapping_password or any(character in wrapping_password for character in "\r\n\0"):
        raise ValueError("Файл должен содержать один непустой пароль PFX Data Protection.")
    environment["Security__DataProtection__KeyDirectory"] = str(protection) if native else "/var/lib/medsoft/data-protection"
    environment["Security__DataProtection__CertificatePath"] = str(protection_pfx) if native else "/run/secrets/data-protection.pfx"
    environment["Security__DataProtection__CertificatePassword"] = wrapping_password
    prefixes = ("ConnectionStrings__", "Authentication__", "Security__", "Database__", "ASPNETCORE_Kestrel__")
    keys = sorted(key for key in environment if key.startswith(prefixes)
                  or key in ("ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS"))
    for key in keys:
        options += ["--env", key]
    return [] if native else options + [CONTAINER_IMAGE], environment


def run(arguments, *, environment=None):
    environment = certificate_environment(arguments.security_directory, environment)
    selected = ["api"] if arguments.api_only else ["web"] if arguments.web_only else ["api", "web"]
    use_container = arguments.api_container and "api" in selected
    if sys.platform == "darwin" and "api" in selected and not use_container:
        raise ValueError("API требует TLS 1.3 на сервере. На macOS используйте --api-container (Docker Desktop).")
    container_name = "medsoft-api-" + secrets.token_hex(5) if use_container else None
    container_arguments, api_environment = api_configuration(arguments.security_directory, environment,
                                                            container_name, native=not use_container) if "api" in selected else ([], {})
    if not arguments.no_build:
        builds = []
        if use_container:
            builds.append([arguments.docker, "build", "--file", str(REPOSITORY / "Dockerfile.api"),
                           "--tag", CONTAINER_IMAGE, str(REPOSITORY)])
        if not use_container or "web" in selected:
            builds.append([arguments.dotnet, "build", str(PROJECTS["web"] if use_container
                                                          else REPOSITORY / "HospitalInformationSystem.slnx")])
        for command in builds:
            build = subprocess.run(command, cwd=REPOSITORY, env=environment, check=False)
            if build.returncode:
                return build.returncode
    processes = []
    try:
        for name in selected:
            command = [arguments.docker, *container_arguments] if name == "api" and use_container else [
                arguments.dotnet, "run", "--no-build", "--project", str(PROJECTS[name]), "--launch-profile", "https"]
            process = subprocess.Popen(command, cwd=REPOSITORY,
                                       env=api_environment if name == "api" else environment,
                                       start_new_session=os.name == "posix")
            processes.append(process)
        if "api" in selected:
            print("API: https://localhost:5201", flush=True)
        if "web" in selected:
            print("Web: https://localhost:5101", flush=True)
        print("Для завершения нажмите Ctrl+C. Доверие к CA настраивается отдельно.", flush=True)
        while True:
            for process in processes:
                result = process.poll()
                if result is not None:
                    return result
            time.sleep(0.2)
    except KeyboardInterrupt:
        return 130
    finally:
        if use_container:
            try:
                subprocess.run([arguments.docker, "stop", "--time", "5", container_name],
                               capture_output=True, text=True, timeout=15, check=False)
                subprocess.run([arguments.docker, "rm", "--force", container_name],
                               capture_output=True, text=True, timeout=15, check=False)
            except (OSError, subprocess.TimeoutExpired):
                pass
        for process in processes:
            stop_process(process)


def run_application(directory, environment, *, no_build=False, docker="docker"):
    return run(argparse.Namespace(
        security_directory=str(directory), api_container=True, api_only=False, web_only=False,
        no_build=no_build, dotnet="dotnet", docker=docker), environment=environment)


def main():
    parser = argparse.ArgumentParser(description="Запустить API и Web с учебным PFX, не передавая пароль в аргументах.")
    parser.add_argument("--security-directory", required=True, help="Каталог, созданный generate-development-certificates.py.")
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--api-only", action="store_true")
    selection.add_argument("--web-only", action="store_true")
    parser.add_argument("--no-build", action="store_true", help="Использовать ранее выполненный dotnet build.")
    parser.add_argument("--dotnet", default="dotnet", help="Путь к .NET SDK.")
    parser.add_argument("--api-container", action="store_true", help="Запускать API в Linux Docker для TLS 1.3 на macOS.")
    parser.add_argument("--docker", default="docker", help="Путь к Docker CLI.")
    return run(parser.parse_args())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
