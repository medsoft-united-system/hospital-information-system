#!/usr/bin/env python3
import argparse
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile


REPOSITORY = Path(__file__).resolve().parent.parent
API_IMAGE = "medsoft-api:lab4-check"
MAX_FRAME_BYTES = 1048576


def load_tool(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPOSITORY / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def snapshot(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def frame_with_token(token):
    data = (REPOSITORY / "tests/Fixtures/Lab4/probe.hl7").read_bytes()
    placeholder = base64.b64encode(b"lab4-fixture-access-token-not-valid")
    if data.count(placeholder) != 1:
        raise ValueError("The C4 probe fixture credential marker is missing.")
    payload = data.replace(placeholder, base64.b64encode(token.encode("utf-8")))
    return b"\x0b" + payload + b"\x1c\r"


def exchange(port, context, token):
    with socket.create_connection(("127.0.0.1", port), timeout=10) as raw:
        with context.wrap_socket(raw, server_hostname="localhost") as connection:
            if connection.version() != "TLSv1.3":
                raise ValueError("MLLP did not negotiate TLS 1.3.")
            connection.settimeout(20)
            connection.sendall(frame_with_token(token))
            result = bytearray()
            while not result.endswith(b"\x1c\r"):
                chunk = connection.recv(min(8192, MAX_FRAME_BYTES + 1 - len(result)))
                if not chunk:
                    raise ConnectionError("MLLP closed the connection before a complete ACK.")
                if len(result) + len(chunk) > MAX_FRAME_BYTES:
                    raise ValueError("MLLP returned an oversized ACK.")
                result.extend(chunk)
    if not result.startswith(b"\x0b") or not result.endswith(b"\x1c\r"):
        raise ValueError("MLLP returned invalid ACK framing.")
    segments = bytes(result[1:-2]).decode("utf-8").split("\r")
    msa = next((segment.split("|") for segment in segments if segment.startswith("MSA|")), [])
    message_id = next(segment.split("|")[9] for segment in frame_with_token(token)[1:-2].decode("utf-8").split("\r")
                      if segment.startswith("MSH|"))
    if len(msa) < 4 or msa[2] != message_id:
        raise ValueError("MLLP ACK correlation is invalid.")
    return msa[1], msa[3]


def start_api(smoke, docker, arguments, profile, connection):
    preparer = load_tool("lab4_smoke_environment", "prepare-lab4-integration.py")
    launcher = load_tool("lab4_smoke_launcher", "run-secure-development.py")
    base = smoke.clean_environment()
    base.update({
        "ConnectionStrings__Postgres": connection,
        "Authentication__Setup__AdministratorUserName": "admin",
        "Security__LoginRateLimit__PermitLimit": "1000",
        "Security__ServiceAuthRateLimit__PermitLimit": "1000",
        "Fhir__PublicBaseUrl": "https://his-api:5201/fhir",
        "AllowedHosts": "localhost;127.0.0.1;his-api",
    })
    environment = preparer.application_environment(arguments, profile, base_environment=base)
    environment = launcher.certificate_environment(profile["httpsDirectory"], environment)
    name = docker.prefix + "-his-api"
    command, environment = launcher.api_configuration(
        profile["httpsDirectory"], environment, name, docker_network=docker.network, network_alias="his-api")
    for index, value in enumerate(command[:-1]):
        if value == "--publish":
            container_port = command[index + 1].rsplit(":", 1)[1]
            command[index + 1] = "127.0.0.1::" + container_port
    command[-1] = API_IMAGE
    command[1:1] = ["--detach", "--label", "medsoft.lab3-smoke=" + docker.run_id]
    docker.remember(name)
    docker.command(*command, environment=environment)
    return name, "https://localhost:" + str(docker.port(name, 5201)), docker.port(name, arguments.mllp_port)


def run(arguments):
    smoke = load_tool("lab4_smoke_isolation", "verify-lab3-integration.py")
    smoke.validate_python_runtime()
    docker = smoke.DockerRun(arguments.docker, secrets.token_hex(6))
    workspace = Path(tempfile.mkdtemp(prefix="medsoft-lab4-launcher-check-")).resolve()
    failure = None
    cleanup = []
    try:
        source = workspace / "working-security"
        manifest = smoke.prepare_security(source, arguments.isu_id)
        preparer = load_tool("lab4_smoke_preparer", "prepare-lab4-integration.py")
        options = argparse.Namespace(
            security_directory=str(source), integration_security_directory=str(workspace / "https-integration"),
            output_directory=str(workspace / "client"), client_ca=None, openssl=None,
            docker_network=docker.network, mllp_port=2575)
        original = snapshot(source)
        profile = preparer.prepare_security(options)
        prepared = snapshot(workspace)
        repeated = preparer.prepare_security(options)
        if repeated != profile or snapshot(source) != original or snapshot(workspace) != prepared:
            raise ValueError("Repeated preparation changed existing keys, certificates or owner token.")
        print("PASS: repeated Lab4 preparation preserves source CA, JWT, Data Protection and owner token.", flush=True)
        docker.command("info", "--format", "{{.ServerVersion}}")
        if not arguments.no_build:
            docker.command("build", "--file", str(REPOSITORY / "Dockerfile.api"), "--tag", API_IMAGE,
                           str(REPOSITORY), timeout=900)
        docker.create_network()
        password = docker.postgres("his-db", "hospital_lab4_launcher")
        connection = "Host=his-db;Database=hospital_lab4_launcher;Username=postgres;Password=" + password
        name, origin, port = start_api(smoke, docker, options, profile, connection)
        https_manifest = json.loads((Path(profile["httpsDirectory"]) / "development-security.json").read_text())
        context = ssl.create_default_context(cafile=https_manifest["caCertificatePath"])
        context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_3
        smoke.wait_ready(smoke.ApiClient(origin, context))
        smoke.verify_tls(origin, https_manifest["caCertificatePath"])
        admin, _, _ = smoke.prepare_accounts(origin, context, https_manifest)
        issued = admin.request("POST", "/api/service-accounts", {"name": "HL7 launcher check", "role": "HL7Import"}, expected=201)
        machine, pair = smoke.machine_client(origin, context, issued["apiKey"])
        if machine.request("GET", "/api/testConnection").get("role") != "HL7Import":
            raise ValueError("HL7Import role was not preserved by HTTPS authentication.")
        print("PASS: fresh API migrations, administrator setup and HL7Import HTTPS authentication.", flush=True)
        mtls = ssl.create_default_context(cafile=https_manifest["caCertificatePath"])
        mtls.minimum_version = mtls.maximum_version = ssl.TLSVersion.TLSv1_3
        mtls.load_cert_chain(profile["clientCertificatePath"], profile["clientKeyPath"])
        if exchange(port, mtls, pair["accessToken"]) != ("AA", "CONNECTED"):
            raise ValueError("Authenticated MLLP probe was rejected.")
        print("PASS: authenticated MLLP probe returned AA/CONNECTED.", flush=True)
        if exchange(port, mtls, "invalid-launcher-check-token")[0] != "AR":
            raise ValueError("MLLP accepted an invalid UAC token.")
        print("PASS: invalid UAC token rejected by MLLP.", flush=True)
        try:
            exchange(port, context, pair["accessToken"])
        except (ssl.SSLError, OSError):
            pass
        else:
            raise ValueError("MLLP accepted a connection without a client certificate.")
        print("PASS: real launcher mounts/ports, HTTPS TLS 1.3, HL7 JWT and MLLP mutual TLS probe.", flush=True)
        docker.command("rm", "--force", name)
        _, second_origin, second_port = start_api(smoke, docker, options, profile, connection)
        smoke.wait_ready(smoke.ApiClient(second_origin, context))
        machine.origin = second_origin
        admin.origin = second_origin
        if machine.request("GET", "/api/testConnection").get("role") != "HL7Import":
            raise ValueError("A service session was lost after restarting the API.")
        if exchange(second_port, mtls, pair["accessToken"]) != ("AA", "CONNECTED"):
            raise ValueError("MLLP did not recover after restarting with existing data.")
        accounts = admin.request("GET", "/api/service-accounts")
        if not any(account["id"] == issued["account"]["id"] for account in accounts):
            raise ValueError("The service account was lost after restarting the API.")
        print("PASS: API restart applies migrations again and preserves administrator, service account and live JWT session.", flush=True)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        failure = error
    finally:
        cleanup = docker.cleanup()
        if not cleanup:
            shutil.rmtree(workspace)
        else:
            print("Cleanup failed; temporary certificate directory retained: " + str(workspace), file=sys.stderr)
    if failure:
        print("Lab4 launcher check failed: " + str(failure), file=sys.stderr)
    if cleanup:
        print("Could not clean up owned temporary Docker resources.", file=sys.stderr)
    if failure or cleanup:
        return 1
    print("Lab4 launcher check passed. Temporary database and containers removed; desktop end-to-end is L8.", flush=True)
    return 0


def main():
    parser = argparse.ArgumentParser(description="Check Lab4 launcher on temporary TLS keys and PostgreSQL; working data is never used.")
    parser.add_argument("--isu-id", default="408944")
    parser.add_argument("--docker", default="docker")
    parser.add_argument("--no-build", action="store_true", help="Reuse medsoft-api:lab4-check built by an earlier check.")
    try:
        return run(parser.parse_args())
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
