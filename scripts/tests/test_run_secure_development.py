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
                                   "data = {'arguments': sys.argv[1:], 'path': os.environ.get('ASPNETCORE_Kestrel__Certificates__Default__Path'), 'password': os.environ.get('ASPNETCORE_Kestrel__Certificates__Default__Password'), 'urls': os.environ.get('ASPNETCORE_URLS'), 'database': os.environ.get('ConnectionStrings__Postgres'), 'key': os.environ.get('Authentication__Jwt__PrivateKeyPath')}\n"
                                   "Path(os.environ['LAUNCHER_PROBE']).write_text(json.dumps(data))\n"
                                   "sys.exit(int(os.environ.get('LAUNCHER_EXIT', '0')))\n")
        self.fake_dotnet.chmod(0o700)
        self.probe = self.root / "probe.json"

    def start(self, *arguments, environment=None, container=True):
        variables = os.environ.copy()
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

    def test_container_database_connection_must_be_explicit(self):
        result = self.start("--api-only", environment={"ConnectionStrings__Postgres": ""})
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.probe.exists())

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
