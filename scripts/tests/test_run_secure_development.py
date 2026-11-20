import argparse
import importlib.util
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


SCRIPT = Path(__file__).resolve().parents[1] / "run-secure-development.py"
spec = importlib.util.spec_from_file_location("run_secure_development", SCRIPT)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class SecureDevelopmentLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="medsoft-secure-launch-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.pfx = self.root / "server.pfx"
        self.password_file = self.root / "password.txt"
        self.pfx.write_bytes(b"test-pfx-placeholder")
        self.password_file.write_text("NeverLog-ThisPassword_42\n")
        self.pfx.chmod(0o600)
        self.password_file.chmod(0o600)
        self.manifest_path = self.root / "development-security.json"
        self.manifest = {"schemaVersion": 1, "serverPfxPath": str(self.pfx),
                         "serverPfxPasswordFile": str(self.password_file)}
        for field, filename in [("jwtPrivateKeyPath", "jwt-signing-private.pem"), ("jwtPublicKeyPath", "jwt-signing-public.pem"),
                                 ("setupTokenFile", "setup-token.txt")]:
            path = self.root / filename
            path.write_text("test-placeholder-" + field)
            path.chmod(0o600)
            self.manifest[field] = str(path)
        protection = self.root / "data-protection"
        protection.mkdir(mode=0o700)
        (protection / "keys").mkdir(mode=0o700)
        for field, filename in [("dataProtectionPfxPath", "wrapping-cert.pfx"),
                                 ("dataProtectionPfxPasswordFile", "wrapping-cert-password.txt")]:
            path = protection / filename
            path.write_text("DataProtection-PrivatePassword_9" if filename.endswith("password.txt") else "test-pfx-placeholder")
            path.chmod(0o600)
            self.manifest[field] = str(path)
        self.manifest["dataProtectionKeyDirectory"] = str(protection / "keys")
        self.manifest_path.write_text(json.dumps(self.manifest))
        self.fake_dotnet = self.root / "dotnet"
        self.fake_dotnet.write_text("#!" + sys.executable + "\n"
                                   "import json, os, sys\n"
                                   "from pathlib import Path\n"
                                   "if sys.argv[1] in ('stop', 'rm'): sys.exit(0)\n"
                                   "if sys.argv[1:3] == ['network', 'inspect']: print('bridge'); sys.exit(0)\n"
                                   "data = {'arguments': sys.argv[1:], 'path': os.environ.get('ASPNETCORE_Kestrel__Certificates__Default__Path'), 'password': os.environ.get('ASPNETCORE_Kestrel__Certificates__Default__Password'), 'urls': os.environ.get('ASPNETCORE_URLS'), 'database': os.environ.get('ConnectionStrings__Postgres'), 'key': os.environ.get('Authentication__Jwt__PrivateKeyPath'), 'fhir': os.environ.get('Fhir__PublicBaseUrl'), 'allowedHosts': os.environ.get('AllowedHosts'), 'hl7': {key: value for key, value in os.environ.items() if key.startswith('Hl7Import__')}}\n"
                                   "Path(os.environ['LAUNCHER_PROBE']).write_text(json.dumps(data))\n"
                                   "sys.exit(int(os.environ.get('LAUNCHER_EXIT', '0')))\n")
        self.fake_dotnet.chmod(0o700)
        self.probe = self.root / "probe.json"

    def start(self, *arguments, environment=None, container=True):
        variables = {key: value for key, value in os.environ.items() if not key.startswith("Hl7Import__")}
        variables["LAUNCHER_PROBE"] = str(self.probe)
        variables["ConnectionStrings__Postgres"] = "Host=host.docker.internal;Database=hospital;Password=HiddenDatabasePassword"
        variables.update(environment or {})
        return subprocess.run([sys.executable, str(SCRIPT), "--security-directory", str(self.root),
                               "--dotnet", str(self.fake_dotnet), "--docker", str(self.fake_dotnet), "--no-build",
                               *(["--api-container"] if container else []), *arguments],
                              capture_output=True, text=True, env=variables, timeout=15)

    def test_api_receives_certificate_via_environment_and_never_as_a_command_argument(self):
        result = self.start("--api-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        self.assertEqual(probe["password"], "NeverLog-ThisPassword_42")
        self.assertEqual(probe["path"], "/run/secrets/server.pfx")
        self.assertEqual(probe["key"], "/run/secrets/jwt-private.pem")
        self.assertEqual(probe["urls"], "https://0.0.0.0:5201")
        self.assertIn(launcher.CONTAINER_IMAGE, probe["arguments"])
        self.assertNotIn(probe["password"], result.stdout + result.stderr + str(probe["arguments"]))
        self.assertNotIn("HiddenDatabasePassword", result.stdout + result.stderr + str(probe["arguments"]))
        mounts = [value for value in probe["arguments"] if value.startswith("type=bind,")]
        self.assertEqual(len(mounts), 6)
        self.assertFalse(any("/ca/" in value for value in mounts))
        self.assertFalse(any("password.txt" in value for value in mounts))
        self.assertFalse(any("src=" + str(self.root) + "," in value for value in mounts))

    def hl7_environment(self, **overrides):
        ca = self.root / "client CA ' with spaces.pem"
        if not ca.exists():
            ca.write_text("-----BEGIN CERTIFICATE-----\npublic test CA\n-----END CERTIFICATE-----\n")
        environment = {
            "Hl7Import__Enabled": "true",
            "Hl7Import__ListenAddress": "0.0.0.0",
            "Hl7Import__ServerCertificate__PfxPath": str(self.pfx),
            "Hl7Import__ServerCertificate__PasswordFile": str(self.password_file),
            "Hl7Import__ClientTrust__CaCertificatePath": str(ca),
            "Hl7Import__Limits__MaxConnections": "8"}
        environment.update(overrides)
        return environment

    def test_hl7_enabled_publishes_loopback_and_mounts_only_required_files_readonly(self):
        environment = self.hl7_environment()
        result = self.start("--api-only", environment=environment)
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        arguments = probe["arguments"]
        self.assertEqual([arguments[index + 1] for index, value in enumerate(arguments) if value == "--publish"],
                         ["127.0.0.1:5201:5201", "127.0.0.1:2575:2575"])
        expected = {
            "Hl7Import__ServerCertificate__PfxPath": "/run/secrets/hl7-server.pfx",
            "Hl7Import__ServerCertificate__PasswordFile": "/run/secrets/hl7-server-password.txt",
            "Hl7Import__ClientTrust__CaCertificatePath": "/run/secrets/hl7-client-ca.pem"}
        for variable, target in expected.items():
            self.assertEqual(probe["hl7"][variable], target)
            self.assertIn("type=bind,src=" + environment[variable] + ",dst=" + target + ",readonly", arguments)
            self.assertIn(variable, arguments)
        self.assertEqual(probe["hl7"]["Hl7Import__ListenAddress"], "0.0.0.0")
        self.assertEqual(probe["hl7"]["Hl7Import__Limits__MaxConnections"], "8")
        self.assertIn("Hl7Import__Limits__MaxConnections", arguments)
        self.assertEqual(len([value for value in arguments if value.startswith("type=bind,")]), 9)
        self.assertNotIn("NeverLog-ThisPassword_42", result.stdout + result.stderr + str(arguments))
        self.assertNotIn("HiddenDatabasePassword", result.stdout + result.stderr + str(arguments))
        self.assertEqual(environment["Hl7Import__ServerCertificate__PfxPath"], str(self.pfx))

    def test_hl7_disabled_does_not_mount_or_read_stale_hl7_paths(self):
        result = self.start("--api-only", environment={
            "Hl7Import__Enabled": "false", "Hl7Import__Port": "not a port",
            "Hl7Import__ServerCertificate__PfxPath": "/missing/private-key"})
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        arguments = probe["arguments"]
        self.assertEqual(probe["hl7"], {"Hl7Import__Enabled": "false"})
        self.assertNotIn("Hl7Import__Port", arguments)
        self.assertNotIn("Hl7Import__ServerCertificate__PfxPath", arguments)
        self.assertEqual(arguments.count("--publish"), 1)
        self.assertEqual(len([value for value in arguments if value.startswith("type=bind,")]), 6)
        self.assertFalse(any("hl7-" in value for value in arguments))

    def test_hl7_custom_port_is_normalized_and_published_consistently(self):
        result = self.start("--api-only", environment=self.hl7_environment(Hl7Import__Port="02675"))
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        self.assertIn("127.0.0.1:2675:2675", probe["arguments"])
        self.assertEqual(probe["hl7"]["Hl7Import__Port"], "2675")

    def test_invalid_hl7_enabled_or_port_is_rejected_before_any_service_starts(self):
        for enabled in ("", "1", "yes", "true,false"):
            with self.subTest(enabled=enabled):
                result = self.start("--api-only", environment=self.hl7_environment(Hl7Import__Enabled=enabled))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.probe.exists())
        for port in ("", "0", "65536", "-1", "5101", "5201", "all", "2575:2575", "1,2575"):
            with self.subTest(port=port):
                result = self.start("--api-only", environment=self.hl7_environment(Hl7Import__Port=port))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.probe.exists())

    def test_hl7_missing_path_is_rejected_before_any_service_starts(self):
        for variable in ("Hl7Import__ServerCertificate__PfxPath", "Hl7Import__ServerCertificate__PasswordFile",
                         "Hl7Import__ClientTrust__CaCertificatePath"):
            for value in ("", str(self.root / "missing")):
                with self.subTest(variable=variable, value=value):
                    environment = self.hl7_environment(**{variable: value})
                    result = self.start("--api-only", environment=environment)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(self.probe.exists())

    @unittest.skipIf(os.name == "nt", "POSIX permissions")
    def test_hl7_password_requires_private_permissions_even_when_https_password_is_separate(self):
        password = self.root / "hl7-password.txt"
        password.write_text("HiddenMllpPassword_22")
        password.chmod(0o644)
        result = self.start("--api-only", environment=self.hl7_environment(
            Hl7Import__ServerCertificate__PasswordFile=str(password)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())
        self.assertNotIn("HiddenMllpPassword_22", result.stdout + result.stderr)

    def test_hl7_symlink_file_or_parent_is_rejected(self):
        aliases = [self.root / "alias.pem", self.root / "linked-directory"]
        aliases[0].symlink_to(self.pfx)
        aliases[1].symlink_to(self.root, target_is_directory=True)
        for path in (aliases[0], aliases[1] / self.pfx.name):
            with self.subTest(path=path):
                result = self.start("--api-only", environment=self.hl7_environment(
                    Hl7Import__ServerCertificate__PfxPath=str(path)))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.probe.exists())

    def test_hl7_ca_with_private_key_is_rejected_without_logging_content(self):
        ca = self.root / "bad-ca.pem"
        ca.write_text("-----BEGIN CERTIFICATE-----\npublic\n-----END CERTIFICATE-----\n"
                      "-----BEGIN PRIVATE KEY-----\nMustNeverLogPrivateKey\n-----END PRIVATE KEY-----\n")
        result = self.start("--api-only", environment=self.hl7_environment(
            Hl7Import__ClientTrust__CaCertificatePath=str(ca)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())
        self.assertNotIn("MustNeverLogPrivateKey", result.stdout + result.stderr)

    def test_hl7_bind_path_with_comma_or_multiline_password_is_rejected(self):
        ca = self.root / "ca,extra.pem"
        ca.write_text("public test CA")
        result = self.start("--api-only", environment=self.hl7_environment(
            Hl7Import__ClientTrust__CaCertificatePath=str(ca)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())
        password = self.root / "hl7-password.txt"
        for value in ("", "FirstSecret\nSecondSecret", "Hidden\0Password"):
            password.write_text(value)
            password.chmod(0o600)
            result = self.start("--api-only", environment=self.hl7_environment(
                Hl7Import__ServerCertificate__PasswordFile=str(password)))
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(self.probe.exists())
            self.assertNotIn("FirstSecret", result.stdout + result.stderr)

    def test_native_hl7_keeps_host_paths_and_does_not_create_docker_arguments(self):
        environment = self.hl7_environment()
        before = environment.copy()
        self.assertEqual(launcher.hl7_configuration(environment, native=True), [])
        for variable in ("Hl7Import__ServerCertificate__PfxPath", "Hl7Import__ServerCertificate__PasswordFile",
                         "Hl7Import__ClientTrust__CaCertificatePath"):
            self.assertEqual(environment[variable], before[variable])

    def test_run_application_forwards_explicit_dotnet_without_mutating_environment(self):
        environment = {"Hl7Import__Enabled": "false"}
        with patch.object(launcher, "run", return_value=17) as run:
            self.assertEqual(launcher.run_application(self.root, environment, dotnet="/custom/dotnet"), 17)
        self.assertEqual(run.call_args.args[0].dotnet, "/custom/dotnet")
        self.assertIs(run.call_args.kwargs["environment"], environment)
        self.assertEqual(environment, {"Hl7Import__Enabled": "false"})

    def test_container_database_connection_must_be_explicit(self):
        result = self.start("--api-only", environment={"ConnectionStrings__Postgres": ""})
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())

    def test_explicit_integration_network_and_public_fhir_address_reach_container(self):
        result = self.start("--api-only", "--docker-network", "medsoft-lab3", "--network-alias", "his-api",
                            environment={"Fhir__PublicBaseUrl": "https://his-api:5201/fhir",
                                         "AllowedHosts": "localhost;his-api"})
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        arguments = probe["arguments"]
        self.assertEqual(arguments[arguments.index("--network") + 1], "medsoft-lab3")
        self.assertEqual(arguments[arguments.index("--network-alias") + 1], "his-api")
        self.assertEqual(probe["fhir"], "https://his-api:5201/fhir")
        self.assertEqual(probe["allowedHosts"], "localhost;his-api")
        self.assertIn("Fhir__PublicBaseUrl", arguments)
        self.assertIn("AllowedHosts", arguments)
        self.assertNotIn("HiddenDatabasePassword", result.stdout + result.stderr + str(arguments))

    def test_invalid_network_and_alias_without_network_are_rejected_before_start(self):
        for arguments in [("--docker-network", "--host"), ("--docker-network", "bad name"),
                          ("--network-alias", "his-api"), ("--docker-network", "bridge", "--network-alias", "his-api"),
                          ("--docker-network", "valid", "--network-alias", "*.invalid")]:
            with self.subTest(arguments=arguments):
                result = self.start("--api-only", *arguments)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.probe.exists())

    def test_existing_nonbridge_network_is_rejected_without_replacement(self):
        with patch.object(launcher.subprocess, "run", return_value=Mock(returncode=0, stdout="overlay\n")) as run:
            with self.assertRaisesRegex(ValueError, "bridge"):
                launcher.ensure_named_network("docker", "medsoft-lab3")
        self.assertEqual(run.call_count, 1)
        self.assertEqual(run.call_args.args[0][-1], "medsoft-lab3")

    def test_concurrent_network_creation_rechecks_existing_bridge(self):
        results = [Mock(returncode=1, stdout=""), Mock(returncode=1, stdout=""), Mock(returncode=0, stdout="bridge\n")]
        with patch.object(launcher.subprocess, "run", side_effect=results) as run:
            launcher.ensure_named_network("docker", "medsoft-lab3")
        self.assertEqual(run.call_count, 3)
        self.assertEqual(run.call_args_list[1].args[0], ["docker", "network", "create", "--driver", "bridge", "medsoft-lab3"])

    def test_preserved_data_protection_directory_can_be_mounted_separately(self):
        preserved = tempfile.TemporaryDirectory(prefix="medsoft-preserved-protection-")
        self.addCleanup(preserved.cleanup)
        preserved_root = Path(preserved.name).resolve()
        for path in (self.root / "data-protection").iterdir():
            shutil.move(str(path), preserved_root / path.name)
        for field, filename in [("dataProtectionPfxPath", "wrapping-cert.pfx"),
                                 ("dataProtectionPfxPasswordFile", "wrapping-cert-password.txt"),
                                 ("dataProtectionKeyDirectory", "keys")]:
            self.manifest[field] = str(preserved_root / filename)
        self.manifest_path.write_text(json.dumps(self.manifest))

        jwt_directory = preserved_root / "jwt"
        jwt_directory.mkdir(mode=0o700)
        for field in ("jwtPrivateKeyPath", "jwtPublicKeyPath"):
            old_path = Path(self.manifest[field])
            new_path = jwt_directory / old_path.name
            shutil.move(str(old_path), new_path)
            self.manifest[field] = str(new_path)
        self.manifest_path.write_text(json.dumps(self.manifest))

        result = self.start("--api-only")

        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        self.assertIn("type=bind,src=" + str(preserved_root / "wrapping-cert.pfx")
                      + ",dst=/run/secrets/data-protection.pfx,readonly", probe["arguments"])
        self.assertIn("type=bind,src=" + str(preserved_root / "keys")
                      + ",dst=/var/lib/medsoft/data-protection", probe["arguments"])
        self.assertNotIn("DataProtection-PrivatePassword_9", str(probe["arguments"]) + result.stdout + result.stderr)

    @unittest.skipUnless(sys.platform == "darwin", "macOS server TLS limitation")
    def test_native_macos_api_is_rejected_instead_of_downgrading_tls(self):
        result = self.start("--api-only", container=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--api-container", result.stderr)
        self.assertFalse(self.probe.exists())

    def test_web_uses_the_same_certificate_and_https_profile(self):
        result = self.start("--web-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        probe = json.loads(self.probe.read_text())
        self.assertEqual(probe["password"], "NeverLog-ThisPassword_42")
        self.assertIn(str(launcher.PROJECTS["web"]), probe["arguments"])
        self.assertIn("https", probe["arguments"])

    def test_application_failure_is_returned_to_the_terminal(self):
        result = self.start("--api-only", environment={"LAUNCHER_EXIT": "72"})
        self.assertEqual(result.returncode, 72)

    def test_explicit_environment_reaches_child_without_changing_global_environment(self):
        before = dict(os.environ)
        environment = dict(os.environ, LAUNCHER_PROBE=str(self.probe),
                           ConnectionStrings__Postgres="Host=demo;Password=OnlyInChildEnvironment")
        arguments = argparse.Namespace(security_directory=str(self.root), api_container=True, api_only=True,
                                       web_only=False, no_build=True, dotnet=str(self.fake_dotnet),
                                       docker=str(self.fake_dotnet))

        self.assertEqual(launcher.run(arguments, environment=environment), 0)

        self.assertEqual(dict(os.environ), before)
        self.assertNotIn("ASPNETCORE_Kestrel__Certificates__Default__Password", environment)
        probe = json.loads(self.probe.read_text())
        self.assertEqual(probe["database"], "Host=demo;Password=OnlyInChildEnvironment")

    def test_manifest_cannot_read_password_outside_the_security_directory(self):
        self.manifest["serverPfxPasswordFile"] = str(self.root.parent / "password.txt")
        self.manifest_path.write_text(json.dumps(self.manifest))
        result = self.start("--api-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())

    @unittest.skipIf(os.name == "nt", "POSIX permissions")
    def test_readable_password_file_is_rejected_before_starting_any_service(self):
        self.password_file.chmod(0o644)
        result = self.start("--api-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())
        self.assertNotIn("NeverLog-ThisPassword_42", result.stdout + result.stderr)

    def test_empty_or_multiline_password_is_rejected(self):
        for value in ("\n", "first\nsecond\n", "bad\0password"):
            with self.subTest(value=value):
                self.password_file.write_text(value)
                result = self.start("--api-only")
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.probe.exists())

    @unittest.skipUnless(os.name == "posix", "POSIX process groups")
    def test_shutdown_terminates_the_dotnet_process_group(self):
        process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True)
        self.addCleanup(lambda: process.kill() if process.poll() is None else None)
        launcher.stop_process(process)
        self.assertIsNotNone(process.poll())
        self.assertEqual(process.returncode, -signal.SIGTERM)


if __name__ == "__main__":
    unittest.main()
