#!/usr/bin/env python3
import argparse
import base64
from datetime import datetime
import hashlib
import hmac
import http.cookiejar
import importlib.util
import json
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


REPOSITORY = Path(__file__).resolve().parent.parent
HELPERS = REPOSITORY / "scripts/lab3-smoke"
API_IMAGE = "medsoft-api:lab3-smoke"
GO_IMAGE = "mcr.microsoft.com/dotnet/aspnet:10.0"
REPORT_KIND = "medsoft-lab3-smoke"
CONFIG_PREFIXES = ("ConnectionStrings__", "Authentication__", "Security__", "Database__", "ASPNETCORE_", "Fhir__", "LIS_", "POSTGRES_")
GO_CHECKS = frozenset({"verified_tls13_ca_hostname", "go_token_testConnection_metadata", "go_four_resources_and_pagination",
    "go_rejected_wrong_patient_before_post", "go_observation_201", "go_completed_readback_and_duplicate_409",
    "go_refresh_keeps_old_access_and_family_deadline", "go_reuse_revokes_family_and_fhir_access",
    "go_key_rotation_revokes_access_refresh_and_key", "go_deactivation_revokes_access_refresh_and_login",
    "go_reactivation_requires_new_session", "go_wire_smoke_complete"})


class SmokeCheckError(ValueError):
    pass


def validate_python_runtime():
    if sys.version_info < (3, 11):
        raise SmokeCheckError("Lab3 smoke requires Python 3.11 or newer with OpenSSL TLS 1.3 support. "
                              "Select a supported interpreter with make lab3-smoke PYTHON=/path/to/python3.")
    if not ssl.HAS_TLSv1_3:
        raise SmokeCheckError("This Python SSL library does not support TLS 1.3. "
                              "Use Python 3.11 or newer linked to OpenSSL with TLS 1.3 support.")
    try:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_3
    except (ValueError, ssl.SSLError):
        raise SmokeCheckError("This Python SSL library cannot enable TLS 1.3. "
                              "Select an OpenSSL-enabled Python interpreter before starting Lab3 smoke.") from None


def clean_environment(environment=None):
    source = os.environ if environment is None else environment
    return {key: value for key, value in source.items() if not key.startswith(CONFIG_PREFIXES)
            and key not in ("AllowedHosts", "DOTNET_ENVIRONMENT", "GH_TOKEN", "GITHUB_TOKEN", "GIT_ASKPASS")}


def private_write(path, value):
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        output.write(value)


def validate_report_path(path):
    path = Path(path).expanduser().absolute()
    if any(item.is_symlink() for item in (path, *path.parents) if str(item) not in ("/tmp", "/var")):
        raise SmokeCheckError("Report path must not contain symbolic links.")
    if path.exists():
        if not path.is_file():
            raise SmokeCheckError("Report destination must be a regular file.")
        try:
            previous = json.loads(path.read_text(encoding="utf-8"))
        except (ValueError, UnicodeError):
            raise SmokeCheckError("Refusing to overwrite an unrelated report file.") from None
        if not isinstance(previous, dict) or previous.get("kind") != REPORT_KIND or previous.get("schemaVersion") != 1:
            raise SmokeCheckError("Refusing to overwrite an unrelated report file.")
    return path


def write_report(path, report):
    path = validate_report_path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.parent / ("." + path.name + "." + secrets.token_hex(6))
    try:
        private_write(temporary, json.dumps(report, ensure_ascii=False, indent=2) + "\n")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, target):
        return None


class ApiClient:
    def __init__(self, origin, context):
        self.origin = origin
        self.access = None
        self.csrf = None
        self.client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(),
            urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def request(self, method, path, payload=None, expected=200, headers=None):
        if not path.startswith("/") or path.startswith("//"):
            raise SmokeCheckError("Smoke HTTP path must be relative to its pinned HIS origin.")
        data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        outgoing = {"Accept": "application/json"}
        if data is not None:
            outgoing["Content-Type"] = "application/json"
        if self.access:
            outgoing["Authorization"] = "Bearer " + self.access
        if self.csrf and method not in ("GET", "HEAD"):
            outgoing["X-CSRF-TOKEN"] = self.csrf
        outgoing.update(headers or {})
        request = urllib.request.Request(self.origin + path, method=method, data=data, headers=outgoing)
        try:
            response = self.client.open(request, timeout=45)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read(4 * 1024 * 1024 + 1)
            if len(raw) > 4 * 1024 * 1024:
                raise SmokeCheckError("Smoke response exceeded its limit.")
            if response.status != expected:
                raise SmokeCheckError("HIS returned unexpected HTTP status for " + method + " " + path.split("?")[0]
                                 + ": " + str(response.status))
            if not raw:
                return None
            try:
                return json.loads(raw)
            except (ValueError, UnicodeError):
                raise SmokeCheckError("HIS returned invalid JSON during smoke.") from None

    def refresh_csrf(self):
        self.csrf = self.request("GET", "/api/auth/csrf")["requestToken"]

    def login(self, user_name, password):
        self.refresh_csrf()
        pair = self.request("POST", "/api/auth/login", {"userName": user_name, "password": password})
        self.access = pair["accessToken"]
        self.refresh_csrf()


def totp(secret, instant=None):
    instant = time.time() if instant is None else instant
    key = base64.b32decode(secret + "=" * (-len(secret) % 8))
    digest = hmac.new(key, struct.pack(">Q", int(instant) // 30), hashlib.sha1).digest()
    offset = digest[-1] & 15
    return f"{(int.from_bytes(digest[offset:offset + 4], 'big') & 0x7fffffff) % 1000000:06d}"


class DockerRun:
    def __init__(self, executable, run_id):
        self.executable = executable
        self.run_id = run_id
        self.prefix = "medsoft-lab3-" + run_id
        self.network = self.prefix + "-network"
        self.containers = []
        self.network_attempted = False

    def command(self, *arguments, environment=None, timeout=180):
        result = subprocess.run([self.executable, *arguments], env=clean_environment() if environment is None else environment,
            capture_output=True, text=True, timeout=timeout, check=False)
        if result.returncode:
            raise SmokeCheckError("Docker " + arguments[0] + " failed during isolated Lab3 smoke (exit " + str(result.returncode) + ").")
        return result.stdout.strip()

    def create_network(self):
        self.network_attempted = True
        self.command("network", "create", "--driver", "bridge", "--label", "medsoft.lab3-smoke=" + self.run_id, self.network)

    def remember(self, name):
        if not name.startswith(self.prefix + "-") or not re.fullmatch(r"[a-z0-9-]+", name):
            raise SmokeCheckError("Refusing an unowned Docker resource name.")
        self.containers.append(name)

    def start(self, name, *arguments, environment=None):
        self.remember(name)
        return self.command("run", "--detach", "--rm", "--init", "--name", name,
            "--label", "medsoft.lab3-smoke=" + self.run_id, "--network", self.network,
            *arguments, environment=environment)

    def postgres(self, suffix, database):
        name = self.prefix + "-" + suffix
        environment = clean_environment()
        environment.update(POSTGRES_PASSWORD=secrets.token_hex(24), POSTGRES_USER="postgres", POSTGRES_DB=database)
        self.start(name, "--network-alias", suffix, "--tmpfs", "/var/lib/postgresql/data:rw",
            "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_USER", "--env", "POSTGRES_DB", "postgres:17-alpine", environment=environment)
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            result = subprocess.run([self.executable, "exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", database],
                capture_output=True, timeout=10, check=False)
            if result.returncode == 0:
                return environment["POSTGRES_PASSWORD"]
            time.sleep(1)
        raise SmokeCheckError("Fresh smoke PostgreSQL was not ready in 90 seconds.")

    def port(self, name, container_port):
        value = self.command("port", name, str(container_port) + "/tcp")
        match = re.fullmatch(r"127\.0\.0\.1:([0-9]{1,5})", value)
        if not match or not 1 <= int(match[1]) <= 65535:
            raise SmokeCheckError("Docker returned an unexpected loopback port mapping.")
        return int(match[1])

    def owned(self, name, kind="container"):
        arguments = ["inspect", "--format", '{{index .Config.Labels "medsoft.lab3-smoke"}}', name]
        if kind == "network":
            arguments = ["network", "inspect", "--format", '{{index .Labels "medsoft.lab3-smoke"}}', name]
        result = subprocess.run([self.executable, *arguments], capture_output=True, text=True, timeout=15, check=False)
        if result.returncode == 0:
            return result.stdout.strip() == self.run_id
        if any(fragment in result.stderr.lower() for fragment in ("no such object", "no such container", "not found")):
            return False
        raise SmokeCheckError("Could not check ownership of a Lab3 Docker resource.")

    def cleanup(self):
        failures = []
        for name in reversed(self.containers):
            try:
                if self.owned(name):
                    self.command("rm", "--force", name, timeout=30)
            except (OSError, ValueError, subprocess.SubprocessError):
                failures.append(name)
        try:
            if self.network_attempted and self.owned(self.network, "network"):
                self.command("network", "rm", self.network, timeout=30)
        except (OSError, ValueError, subprocess.SubprocessError):
            failures.append(self.network)
        return failures


def load_launcher():
    spec = importlib.util.spec_from_file_location("lab3_secure_launcher", REPOSITORY / "scripts/run-secure-development.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def prepare_security(directory, isu_id):
    result = subprocess.run([sys.executable, str(REPOSITORY / "scripts/generate-development-certificates.py"),
        "--isu-id", isu_id, "--output-dir", str(directory), "--dns-name", "his-api"],
        env=clean_environment(), capture_output=True, text=True, timeout=180, check=False)
    if result.returncode:
        raise SmokeCheckError("Could not generate fresh Lab3 certificates.")
    return json.loads((directory / "development-security.json").read_text())


def start_api(docker, directory, connection):
    environment = clean_environment()
    environment.update({"ConnectionStrings__Postgres": connection, "Database__ApplyMigrations": "true", "Database__Seed": "false",
        "Authentication__Setup__AdministratorUserName": "admin", "Security__LoginRateLimit__PermitLimit": "1000",
        "Security__ServiceAuthRateLimit__PermitLimit": "1000", "Fhir__PublicBaseUrl": "https://his-api:5201/fhir",
        "AllowedHosts": "localhost;his-api;127.0.0.1"})
    launcher = load_launcher()
    environment = launcher.certificate_environment(directory, environment)
    name = docker.prefix + "-his-api"
    arguments, environment = launcher.api_configuration(directory, environment, name, docker_network=docker.network, network_alias="his-api")
    arguments[arguments.index("--publish") + 1] = "127.0.0.1::5201"
    arguments[-1] = API_IMAGE
    arguments[1:1] = ["--detach", "--label", "medsoft.lab3-smoke=" + docker.run_id]
    docker.remember(name)
    docker.command(*arguments, environment=environment)
    return "https://localhost:" + str(docker.port(name, 5201))


def wait_ready(client):
    deadline = time.monotonic() + 100
    while time.monotonic() < deadline:
        try:
            if client.request("GET", "/health/ready").get("status") == "ready":
                return
        except (urllib.error.URLError, OSError, ValueError):
            pass
        time.sleep(1)
    raise SmokeCheckError("Fresh HIS API was not ready in 100 seconds.")


def verify_tls(origin, ca):
    address = urllib.parse.urlsplit(origin)
    trusted = ssl.create_default_context(cafile=str(ca))
    trusted.minimum_version = trusted.maximum_version = ssl.TLSVersion.TLSv1_3
    with socket.create_connection(("127.0.0.1", address.port), timeout=10) as raw:
        with trusted.wrap_socket(raw, server_hostname="localhost") as connection:
            if connection.version() != "TLSv1.3":
                raise SmokeCheckError("HIS did not negotiate TLS 1.3.")
    for context, host in [(ssl.create_default_context(), "localhost"), (trusted, "wrong.invalid")]:
        try:
            with socket.create_connection(("127.0.0.1", address.port), timeout=10) as raw:
                context.wrap_socket(raw, server_hostname=host).close()
        except ssl.SSLCertVerificationError:
            pass
        else:
            raise SmokeCheckError("HIS TLS accepted an untrusted CA or mismatched hostname.")
    legacy = ssl.create_default_context(cafile=str(ca))
    legacy.minimum_version = legacy.maximum_version = ssl.TLSVersion.TLSv1_2
    try:
        with socket.create_connection(("127.0.0.1", address.port), timeout=10) as raw:
            legacy.wrap_socket(raw, server_hostname="localhost").close()
    except ssl.SSLError:
        return
    raise SmokeCheckError("HIS accepted TLS 1.2.")


def prepare_accounts(origin, context, manifest):
    admin = ApiClient(origin, context)
    admin.refresh_csrf()
    owner = Path(manifest["setupTokenFile"]).read_text().strip()
    password = "Lab3.Aa9!" + secrets.token_urlsafe(24)
    headers = {"X-Setup-Token": owner}
    challenge = admin.request("POST", "/api/auth/setup/begin", {"password": password, "confirmPassword": password}, headers=headers)
    step = int(time.time()) // 30
    admin.request("POST", "/api/auth/setup/confirm", {"challengeId": challenge["challengeId"],
        "code": totp(challenge["secretKey"], step * 30)}, expected=204, headers=headers)
    print("Waiting for the next administrator TOTP step.", flush=True)
    deadline = time.monotonic() + 35
    while int(time.time()) // 30 <= step:
        if time.monotonic() >= deadline:
            raise SmokeCheckError("The next TOTP step did not start within 35 seconds.")
        time.sleep(0.25)
    admin.request("POST", "/api/auth/admin/login", {"userName": "admin", "password": password})
    pair = admin.request("POST", "/api/auth/admin/confirm", {"code": totp(challenge["secretKey"])})
    admin.access = pair["accessToken"]
    admin.refresh_csrf()
    clinicians = []
    for role, name in [("doctor", "doctor.smoke"), ("nurse", "reader.smoke")]:
        worker = admin.request("POST", "/api/medical-workers", {"firstName": "Учебный", "lastName": "Проверочный",
            "position": "Врач" if role == "doctor" else "Медсестра", "role": role}, expected=201)
        account = admin.request("POST", "/api/medical-workers/" + worker["id"] + "/account", {"userName": name}, expected=201)
        client = ApiClient(origin, context)
        client.login(account["account"]["userName"], account["generatedPassword"])
        clinicians.append(client)
    return admin, *clinicians


def prepare_clinical(doctor):
    catalog = [item for item in doctor.request("GET", "/api/laboratory-tests") if item["isActive"]]
    if len(catalog) < 7:
        raise SmokeCheckError("Seven LOINC definitions were not initialized.")
    patient = doctor.request("POST", "/api/patients", {"firstName": "Андрей", "lastName": "Лабораторный", "middleName": "Учебный",
        "birthDate": "1990-01-01", "sex": "Male", "comment": "Synthetic isolated Lab3 smoke"}, expected=201)
    record = doctor.request("POST", "/api/patients/" + patient["id"] + "/medical-record", expected=201)
    path = "/api/medical-records/" + record["id"] + "/laboratory-analysis-requests"
    orders = doctor.request("POST", path, {"laboratoryTestDefinitionIds": [item["id"] for item in catalog[:3]],
        "expectedVersions": {item["id"]: item["version"] for item in catalog[:3]}, "comment": "Учебное назначение"}, expected=201)
    repeated = doctor.request("POST", path, {"laboratoryTestDefinitionIds": [catalog[0]["id"]],
        "expectedVersions": {catalog[0]["id"]: catalog[0]["version"]}, "comment": "Повторное назначение того же анализа"}, expected=201)
    if len(orders) != 3 or len(repeated) != 1 or len({item["id"] for item in orders + repeated}) != 4:
        raise SmokeCheckError("Batch/repeated orders did not receive distinct IDs.")
    return record, orders


def machine_client(origin, context, key):
    client = ApiClient(origin, context)
    pair = client.request("POST", "/api/service-auth/token", {"apiKey": key})
    client.access = pair["accessToken"]
    return client, pair


def python_wire(origin, context, key, order):
    client, pair = machine_client(origin, context, key)
    probe = client.request("GET", "/api/testConnection")
    if probe.get("status") != "connected" or probe.get("role") != "LIS":
        raise SmokeCheckError("Service connection was not confirmed.")
    for kind, identifier in [("Patient", order["patientId"]), ("Practitioner", order["requestedByMedicalWorkerId"]), ("ServiceRequest", order["id"])]:
        client.request("GET", "/fhir/" + kind + "/" + identifier)
    body = {"resourceType": "Observation", "status": "final", "code": {"coding": [{"system": "http://loinc.org", "code": order["loincCode"]}]},
        "subject": {"reference": "Patient/" + order["patientId"]}, "basedOn": [{"reference": "ServiceRequest/" + order["id"]}],
        "issued": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "valueString": "5.6"}
    headers = {"Content-Type": "application/fhir+json"}
    created = client.request("POST", "/fhir/Observation", body, expected=201, headers=headers)
    client.request("GET", "/fhir/Observation/" + created["id"])
    completed = client.request("GET", "/fhir/ServiceRequest/" + order["id"])
    if completed.get("status") != "completed":
        raise SmokeCheckError("FHIR order was not completed.")
    duplicate = client.request("POST", "/fhir/Observation", body, expected=409, headers=headers)
    if duplicate.get("resourceType") != "OperationOutcome":
        raise SmokeCheckError("Repeated result did not use FHIR OperationOutcome.")
    refreshed = client.request("POST", "/api/service-auth/refresh", {"refreshToken": pair["refreshToken"]})
    client.request("GET", "/api/testConnection")
    if refreshed["refreshTokenExpiresAt"] != pair["refreshTokenExpiresAt"]:
        raise SmokeCheckError("Refresh extended the family lifetime.")
    client.access = refreshed["accessToken"]
    client.request("POST", "/api/service-auth/refresh", {"refreshToken": pair["refreshToken"]}, expected=401)
    client.request("GET", "/api/testConnection", expected=401)
    return {"id": created["id"], "valueString": created["valueString"], "issued": created["issued"]}


def copy_lis_source(source, destination):
    source = Path(source).expanduser().resolve()
    module = source / "lis" if (source / "lis/go.mod").is_file() else source
    if module.is_symlink() or (module / "go.mod").is_symlink() or not (module / "go.mod").is_file():
        raise SmokeCheckError("--lis-source must name the Go LIS checkout or its lis module.")
    if not re.search(r"(?m)^module\s+laboratory-information-system/lis\s*$", (module / "go.mod").read_text()):
        raise SmokeCheckError("The LIS module does not match the supported integration contract.")
    destination.mkdir(mode=0o700)
    digest = hashlib.sha256()
    allowed = {".go", ".mod", ".sum", ".sql", ".html", ".css", ".js", ".svg"}
    for item in sorted(module.rglob("*")):
        relative = item.relative_to(module)
        if relative.as_posix() not in ("go.mod", "go.sum") and not relative.is_relative_to("cmd/lis") and relative.parts[0] not in ("internal", "migrations", "web"):
            continue
        if any(part.startswith(".") or part in ("node_modules", "vendor", "his-smoke") for part in relative.parts):
            continue
        if item.is_symlink():
            raise SmokeCheckError("LIS build source must not contain symbolic links.")
        if not item.is_file() or item.suffix not in allowed:
            continue
        data = item.read_bytes()
        digest.update(relative.as_posix().encode() + b"\0" + data)
        target = destination / relative
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        target.write_bytes(data)
    harness = destination / "cmd/his-smoke"
    harness.mkdir(parents=True)
    shutil.copyfile(HELPERS / "main.go", harness / "main.go")
    return digest.hexdigest()


def docker_architecture(docker):
    architecture = docker.command("info", "--format", "{{.Architecture}}")
    mapping = {"aarch64": "arm64", "arm64": "arm64", "x86_64": "amd64", "amd64": "amd64"}
    if architecture not in mapping:
        raise SmokeCheckError("Unsupported Docker architecture for Go smoke.")
    return mapping[architecture]


def build_go(docker, source, workspace, go):
    module = workspace / "lis-module"
    fingerprint = copy_lis_source(source, module)
    environment = clean_environment()
    environment.update(GOOS="linux", GOARCH=docker_architecture(docker), CGO_ENABLED="0")
    binaries = {}
    for name, package in [("lis", "./cmd/lis"), ("smoke", "./cmd/his-smoke")]:
        output = workspace / ("go-" + name)
        result = subprocess.run([go, "build", "-trimpath", "-o", str(output), package], cwd=module,
            env=environment, capture_output=True, text=True, timeout=600, check=False)
        if result.returncode:
            raise SmokeCheckError("The pinned Go LIS source could not build " + name + " (no production source was changed).")
        binaries[name] = output
    return binaries, fingerprint


def container_user():
    return ["--user", str(os.getuid()) + ":" + str(os.getgid())] if os.name == "posix" else []


def start_lis(docker, binary, workspace, ca):
    password = docker.postgres("lis-db", "lis")
    private = workspace / "lis-private"
    private.mkdir(mode=0o700)
    (private / "master.key").write_bytes(secrets.token_bytes(32))
    (private / "master.key").chmod(0o600)
    shutil.copyfile(ca, private / "ca.pem")
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    authority = "127.0.0.1:" + str(port)
    environment = clean_environment()
    environment.update(LIS_HTTP_ADDR="0.0.0.0:8080", LIS_DB_DSN="postgresql://postgres:" + password + "@lis-db:5432/lis?sslmode=disable",
        LIS_MASTER_KEY_FILE="/run/lis/master.key", LIS_HIS_CA_FILE="/run/lis/ca.pem", LIS_ALLOWED_HOSTS=authority)
    name = docker.prefix + "-lis"
    docker.start(name, *container_user(), "--publish", authority + ":8080",
        "--mount", "type=bind,src=" + str(binary) + ",dst=/usr/local/bin/lis,readonly",
        "--mount", "type=bind,src=" + str(private) + ",dst=/run/lis,readonly",
        "--env", "LIS_HTTP_ADDR", "--env", "LIS_DB_DSN", "--env", "LIS_MASTER_KEY_FILE", "--env", "LIS_HIS_CA_FILE", "--env", "LIS_ALLOWED_HOSTS",
        "--entrypoint", "/usr/local/bin/lis", GO_IMAGE, environment=environment)
    if docker.port(name, 8080) != port:
        raise SmokeCheckError("Docker published an unexpected LIS port.")
    origin = "http://" + authority
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        try:
            with client.open(origin + "/health/ready", timeout=3) as response:
                if response.status in (200, 204):
                    return origin
        except (urllib.error.URLError, OSError):
            time.sleep(1)
    raise SmokeCheckError("Actual isolated Go LIS was not ready in 60 seconds.")


def connection_form(lis_origin, key_file, identity="new"):
    result = subprocess.run([sys.executable, str(HELPERS / "connection-smoke.py"), "--lis-url", lis_origin,
        "--his-origin", "https://his-api:5201", "--key-file", str(key_file), "--identity", identity],
        env=clean_environment(), capture_output=True, text=True, timeout=90, check=False)
    if result.returncode:
        raise SmokeCheckError("Actual LIS protected connection form did not confirm HIS and FHIR R5.")


def go_wire(docker, binary, workspace, ca, key_file, order, admin, account):
    client = workspace / "go-client"
    client.mkdir(mode=0o700)
    shutil.copyfile(ca, client / "ca.pem")
    shutil.copyfile(key_file, client / "api-key")
    (client / "api-key").chmod(0o600)
    config = {"origin": "https://his-api:5201", "caFile": "/run/client/ca.pem", "keyFile": "/run/client/api-key",
        "requestId": order["id"], "result": "142", "reportFile": "/run/client/report.json"}
    private_write(client / "config.json", json.dumps(config))
    name = docker.prefix + "-go-smoke"
    docker.remember(name)
    command = [docker.executable, "run", "--rm", "--init", "--interactive", "--name", name,
        "--label", "medsoft.lab3-smoke=" + docker.run_id, "--network", docker.network, *container_user(),
        "--mount", "type=bind,src=" + str(binary) + ",dst=/usr/local/bin/smoke,readonly",
        "--mount", "type=bind,src=" + str(client) + ",dst=/run/client",
        "--entrypoint", "/usr/local/bin/smoke", GO_IMAGE, "--config", "/run/client/config.json", "--interactive"]
    process = subprocess.Popen(command, env=clean_environment(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, bufsize=1)
    messages = queue.Queue()
    def read_lines():
        for line in process.stdout:
            messages.put(line)
        messages.put(None)
    threading.Thread(target=read_lines, daemon=True).start()
    deadline = time.monotonic() + 180
    stages = []
    try:
        while time.monotonic() < deadline:
            try:
                line = messages.get(timeout=1)
            except queue.Empty:
                continue
            if line is None:
                break
            try:
                message = json.loads(line)
            except ValueError:
                raise SmokeCheckError("Go smoke emitted an invalid status record.") from None
            if message.get("status") == "failed":
                raise SmokeCheckError("Actual Go LIS client smoke failed; upstream errors are withheld to protect credentials.")
            stage = message.get("stage")
            if message.get("status") == "passed":
                if not isinstance(stage, str) or stage not in GO_CHECKS:
                    raise SmokeCheckError("Go smoke emitted an unknown completed check.")
                stages.append(stage)
            if message.get("status") != "waiting":
                continue
            path = "/api/service-accounts/" + account["id"]
            current = admin.request("GET", path)
            if stage == "awaiting_key_rotation_and_key_file_update":
                rotated = admin.request("POST", path + "/rotate-key", {"version": current["version"]})
                (client / "api-key").write_text(rotated["apiKey"])
                key_file.write_text(rotated["apiKey"])
            elif stage in ("awaiting_account_deactivation", "awaiting_account_reactivation"):
                admin.request("PUT", path + "/active", {"isActive": stage == "awaiting_account_reactivation", "version": current["version"]})
            else:
                raise SmokeCheckError("Go smoke requested an unknown operator action.")
            process.stdin.write("continue\n")
            process.stdin.flush()
        else:
            raise SmokeCheckError("Go smoke exceeded its deadline.")
        process.stdin.close()
        if process.wait(timeout=15) != 0 or set(stages) != GO_CHECKS:
            raise SmokeCheckError("Actual Go LIS wire smoke did not complete.")
        report = json.loads((client / "report.json").read_text())
        return {"id": report["observationId"], "valueString": report["valueString"], "issued": report["issued"]}, stages
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def run(arguments):
    if not re.fullmatch(r"[1-9][0-9]{2,11}", arguments.isu_id):
        raise SmokeCheckError("ISU ID must contain 3–12 digits.")
    report_path = validate_report_path(arguments.report)
    run_id = secrets.token_hex(6)
    docker = DockerRun(arguments.docker, run_id)
    report = {"kind": REPORT_KIND, "schemaVersion": 1, "status": "running", "runId": run_id,
        "workingDatabaseUsed": False, "actualGoLisClientChecked": False, "actualLisLocalHistoryChecked": False, "checks": []}
    failure = None
    temporary = None
    stage = "python_runtime"
    try:
        validate_python_runtime()
        stage = "docker_availability"
        docker.command("info")
        stage = "api_image"
        if arguments.no_build:
            docker.command("image", "inspect", API_IMAGE)
        else:
            print("Building the dedicated Lab3 smoke API image.", flush=True)
            docker.command("build", "--file", str(REPOSITORY / "Dockerfile.api"), "--tag", API_IMAGE, str(REPOSITORY), timeout=900)
        temporary = tempfile.TemporaryDirectory(prefix="medsoft-lab3-")
        workspace = Path(temporary.name).resolve()
        binaries = None
        if arguments.lis_source:
            stage = "go_build"
            print("Building an isolated copy of the supplied Go LIS.", flush=True)
            binaries, report["lisSourceFingerprintSha256"] = build_go(docker, arguments.lis_source, workspace, arguments.go)
        stage = "fresh_his_stand"
        security = workspace / "security"
        manifest = prepare_security(security, arguments.isu_id)
        ca = Path(manifest["caCertificatePath"])
        docker.create_network()
        password = docker.postgres("his-db", "hospital")
        origin = start_api(docker, security, "Host=his-db;Database=hospital;Username=postgres;Password=" + password)
        context = ssl.create_default_context(cafile=str(ca))
        context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_3
        wait_ready(ApiClient(origin, context))
        stage = "verified_tls"
        verify_tls(origin, ca)
        report["checks"].append("tls13_ca_hostname_and_tls12_rejection")
        print("Configuring a fresh administrator and synthetic clinical scenario.", flush=True)
        stage = "administrator_and_clinical_accounts"
        admin, doctor, reader = prepare_accounts(origin, context, manifest)
        stage = "clinical_scenario"
        record, orders = prepare_clinical(doctor)
        created = admin.request("POST", "/api/service-accounts", {"name": "LIS isolated smoke"}, expected=201)
        key_file = workspace / "service-api-key"
        private_write(key_file, created["apiKey"])
        stage = "his_fhir_and_service_auth"
        expected_results = [python_wire(origin, context, created["apiKey"], orders[0])]
        report["checks"].extend(["admin_setup_totp", "doctor_reader_roles", "seven_loinc_definitions", "batch_three_and_repeated_order",
            "fhir_201_readback_completed_duplicate409", "refresh_old_access_and_reuse_revocation"])
        if binaries:
            stage = "actual_lis_start"
            lis_origin = start_lis(docker, binaries["lis"], workspace, ca)
            stage = "actual_lis_connection"
            connection_form(lis_origin, key_file)
            stage = "actual_go_fhir_client"
            result, stages = go_wire(docker, binaries["smoke"], workspace, ca, key_file, orders[1], admin, created["account"])
            expected_results.append(result)
            stage = "actual_lis_reconnection"
            connection_form(lis_origin, key_file, "same")
            report["actualGoLisClientChecked"] = True
            report["checks"].extend(["actual_lis_connection_form_csrf_and_settings_persistence", *stages])
        else:
            machine, pair = machine_client(origin, context, key_file.read_text())
            path = "/api/service-accounts/" + created["account"]["id"]
            current = admin.request("GET", path)
            rotated = admin.request("POST", path + "/rotate-key", {"version": current["version"]})
            machine.request("GET", "/api/testConnection", expected=401)
            machine.request("POST", "/api/service-auth/refresh", {"refreshToken": pair["refreshToken"]}, expected=401)
            machine, pair = machine_client(origin, context, rotated["apiKey"])
            current = admin.request("GET", path)
            inactive = admin.request("PUT", path + "/active", {"isActive": False, "version": current["version"]})
            machine.request("GET", "/api/testConnection", expected=401)
            admin.request("PUT", path + "/active", {"isActive": True, "version": inactive["account"]["version"]})
            machine.request("GET", "/api/testConnection", expected=401)
            report["checks"].append("key_rotation_deactivation_reactivation_does_not_restore_access")
        stage = "clinical_result_readback"
        expected = {item["id"]: (item["valueString"], datetime.fromisoformat(item["issued"])) for item in expected_results}
        for client in (doctor, reader):
            clinical = client.request("GET", "/api/medical-records/" + record["id"])
            results = [item["result"] for item in clinical["laboratoryAnalysisRequests"] if item.get("result")]
            found = {item["id"]: (item["value"], datetime.fromisoformat(item["issuedAtUtc"])) for item in results}
            if found != expected:
                raise SmokeCheckError("Doctor/reader EMR did not show the expected result values and dates.")
        report["checks"].append("results_visible_to_doctor_and_reader")
        report["status"] = "passed"
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        failure = error
        report["status"] = "failed"
        report["failureType"] = type(error).__name__
        report["failedStage"] = stage
        if isinstance(error, SmokeCheckError):
            report["failureMessage"] = str(error)
    except KeyboardInterrupt:
        report["status"] = "interrupted"
        report["failedStage"] = stage
        raise
    finally:
        leftovers = docker.cleanup()
        if temporary is not None:
            temporary.cleanup()
        report["cleanup"] = "passed" if not leftovers else "needs_attention"
        if leftovers:
            report["leftoverResources"] = leftovers
            report["status"] = "failed"
        write_report(report_path, report)
    print("Lab3 smoke " + report["status"] + ". Report: " + str(report_path), flush=True)
    if failure is not None:
        print("Lab3 stage failed " + stage + " (" + type(failure).__name__ + ").", file=sys.stderr)
        if isinstance(failure, SmokeCheckError):
            print(str(failure), file=sys.stderr)
    return 0 if report["status"] == "passed" else 1


def main():
    parser = argparse.ArgumentParser(description="Verify Lab3 on fresh isolated Docker resources; never use the working database.")
    parser.add_argument("--isu-id", default="408944")
    parser.add_argument("--report", default=str(REPOSITORY / "artifacts/lab3/integration-report.json"))
    parser.add_argument("--lis-source", help="Go LIS checkout or its lis module; a build-only copy is staged outside the repository.")
    parser.add_argument("--no-build", action="store_true", help="Reuse the dedicated medsoft-api:lab3-smoke image.")
    parser.add_argument("--docker", default="docker")
    parser.add_argument("--go", default="go")
    return run(parser.parse_args())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print("Lab3 smoke could not start (" + type(error).__name__ + ").", file=sys.stderr)
        sys.exit(1)
