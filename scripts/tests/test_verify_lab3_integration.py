import argparse
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import MagicMock, Mock, patch
import urllib.error


SCRIPT = Path(__file__).resolve().parents[1] / "verify-lab3-integration.py"
spec = importlib.util.spec_from_file_location("lab3_integration", SCRIPT)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class Lab3IsolationTests(unittest.TestCase):
    def test_lis_allowed_host_matches_the_published_loopback_authority(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            ca = workspace / "ca.pem"
            ca.write_text("public test CA")
            docker = Mock(prefix="medsoft-test")
            docker.postgres.return_value = "temporary-password"
            docker.port.return_value = 54321
            client = MagicMock()
            client.open.return_value.__enter__.return_value.status = 200
            with patch.object(smoke.socket, "socket") as socket, \
                    patch.object(smoke.urllib.request, "build_opener", return_value=client):
                socket.return_value.__enter__.return_value.getsockname.return_value = ("127.0.0.1", 54321)

                origin = smoke.start_lis(docker, workspace / "lis", workspace, ca)

            self.assertEqual(origin, "http://127.0.0.1:54321")
            args, kwargs = docker.start.call_args
            self.assertEqual(args[args.index("--publish") + 1], "127.0.0.1:54321:8080")
            self.assertEqual(kwargs["environment"]["LIS_ALLOWED_HOSTS"], "127.0.0.1:54321")
            self.assertIn("LIS_ALLOWED_HOSTS", args)
            client.open.assert_called_once_with(origin + "/health/ready", timeout=3)

    def test_python_runtime_rejects_old_python_before_docker_or_stand_creation(self):
        with tempfile.TemporaryDirectory() as directory:
            args = argparse.Namespace(isu_id="408944", report=Path(directory) / "report.json", docker="docker",
                                      lis_source=None, go="go", no_build=True)
            with patch.object(smoke.sys, "version_info", (3, 9, 6)), \
                    patch.object(smoke.DockerRun, "command") as command, \
                    patch.object(smoke, "prepare_security") as security, \
                    redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                self.assertEqual(smoke.run(args), 1)
            command.assert_not_called()
            security.assert_not_called()
            report = json.loads(args.report.read_text())
            self.assertEqual(report["failedStage"], "python_runtime")
            self.assertEqual(report["cleanup"], "passed")
            self.assertIn("Python 3.11", report["failureMessage"])

    def test_python_runtime_rejects_ssl_without_tls13(self):
        with patch.object(smoke.sys, "version_info", (3, 11, 0)), patch.object(smoke.ssl, "HAS_TLSv1_3", False):
            with self.assertRaisesRegex(smoke.SmokeCheckError, "does not support TLS 1.3"):
                smoke.validate_python_runtime()

    def test_python_runtime_rejects_unusable_tls13_context(self):
        with patch.object(smoke.sys, "version_info", (3, 11, 0)), patch.object(smoke.ssl, "HAS_TLSv1_3", True), \
                patch.object(smoke.ssl, "SSLContext", side_effect=ValueError("unavailable")):
            with self.assertRaisesRegex(smoke.SmokeCheckError, "cannot enable TLS 1.3"):
                smoke.validate_python_runtime()

    def test_python_runtime_accepts_tls13_and_rfc3339_z_dates(self):
        smoke.validate_python_runtime()
        self.assertEqual(smoke.datetime.fromisoformat("2026-11-11T12:13:14.123Z").utcoffset().total_seconds(), 0)

    def test_working_his_and_lis_settings_are_filtered_from_child_environment(self):
        inherited = {"PATH": "/bin", "DOCKER_CONTEXT": "desktop-linux", "ConnectionStrings__Postgres": "working-db",
                     "Authentication__Jwt__PrivateKeyPath": "working-key", "Security__DataProtection__KeyDirectory": "working-ring",
                     "Fhir__PublicBaseUrl": "working-url", "LIS_DB_DSN": "working-lis", "POSTGRES_PASSWORD": "working-password",
                     "DOTNET_ENVIRONMENT": "Production", "GITHUB_TOKEN": "credential", "AllowedHosts": "working-host"}
        self.assertEqual(smoke.clean_environment(inherited), {"PATH": "/bin", "DOCKER_CONTEXT": "desktop-linux"})
        self.assertEqual(inherited["ConnectionStrings__Postgres"], "working-db")

    def test_postgres_stays_on_private_network_with_tmpfs_and_password_only_in_environment(self):
        docker = smoke.DockerRun("docker", "0123456789ab")
        with patch.object(docker, "command") as command, patch.object(smoke.subprocess, "run", return_value=Mock(returncode=0)):
            password = docker.postgres("his-db", "hospital")
        args, kwargs = command.call_args
        self.assertIn("--tmpfs", args)
        self.assertIn(docker.network, args)
        self.assertIn("medsoft.lab3-smoke=0123456789ab", args)
        self.assertNotIn("--publish", args)
        self.assertNotIn("--volume", args)
        self.assertNotIn(password, " ".join(args))
        self.assertEqual(kwargs["environment"]["POSTGRES_PASSWORD"], password)
        self.assertEqual(docker.containers, ["medsoft-lab3-0123456789ab-his-db"])

    def test_cleanup_checks_ownership_and_reports_daemon_failure(self):
        docker = smoke.DockerRun("docker", "0123456789ab")
        own = docker.prefix + "-his-db"
        foreign = docker.prefix + "-lis"
        docker.remember(own)
        docker.remember(foreign)
        docker.network_attempted = True
        inspect_results = [subprocess.CompletedProcess([], 0, "other-run\n", ""),
                           subprocess.CompletedProcess([], 0, docker.run_id + "\n", ""),
                           subprocess.CompletedProcess([], 1, "", "Cannot connect to the Docker daemon")]
        with patch.object(smoke.subprocess, "run", side_effect=inspect_results), patch.object(docker, "command") as command:
            self.assertEqual(docker.cleanup(), [docker.network])
        command.assert_called_once_with("rm", "--force", own, timeout=30)

    def test_missing_auto_removed_container_is_clean(self):
        docker = smoke.DockerRun("docker", "0123456789ab")
        docker.remember(docker.prefix + "-his-api")
        missing = subprocess.CompletedProcess([], 1, "", "Error: No such object: medsoft-test")
        with patch.object(smoke.subprocess, "run", return_value=missing), patch.object(docker, "command") as command:
            self.assertEqual(docker.cleanup(), [])
        command.assert_not_called()

    def test_report_replaces_only_previous_smoke_report(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "report.json"
            target.write_text('{"keep":true}')
            with self.assertRaisesRegex(ValueError, "unrelated"):
                smoke.write_report(target, {"status": "passed"})
            self.assertEqual(target.read_text(), '{"keep":true}')
            target.unlink()
            report = {"kind": smoke.REPORT_KIND, "schemaVersion": 1, "status": "passed"}
            smoke.write_report(target, report)
            smoke.write_report(target, report | {"status": "failed"})
            self.assertEqual(json.loads(target.read_text())["status"], "failed")
            self.assertEqual(target.stat().st_mode & 0o777, 0o600)
            alias = Path(directory) / "alias.json"
            alias.symlink_to(target)
            with self.assertRaisesRegex(ValueError, "symbolic links"):
                smoke.write_report(alias, report)

    def test_build_copy_ignores_working_configs_keys_and_existing_harness(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            for filename, data in {"go.mod": "module laboratory-information-system/lis\n", "cmd/lis/main.go": "package main\n",
                                   "internal/infrastructure/secrets/cipher.go": "package secrets\n", "internal/live.json": '{"password":"local"}',
                                   "internal/master.key": "local-key", "cmd/his-smoke/main.go": "old-harness", ".env": "local-password"}.items():
                target = source / filename
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(data)
            staged = root / "staged"
            fingerprint = smoke.copy_lis_source(source, staged)
            self.assertTrue((staged / "internal/infrastructure/secrets/cipher.go").is_file())
            self.assertFalse((staged / ".env").exists())
            self.assertFalse((staged / "internal/master.key").exists())
            self.assertFalse((staged / "internal/live.json").exists())
            self.assertEqual((staged / "cmd/his-smoke/main.go").read_bytes(), (smoke.HELPERS / "main.go").read_bytes())
            (source / "internal/live.json").write_text('{"password":"different"}')
            self.assertEqual(smoke.copy_lis_source(source, root / "second"), fingerprint)

    def test_failed_stand_cleans_docker_before_deleting_temporary_certificate_files(self):
        with tempfile.TemporaryDirectory() as report_directory:
            args = argparse.Namespace(isu_id="408944", report=Path(report_directory) / "report.json", docker="docker",
                                      lis_source=None, go="go", no_build=True)
            docker = Mock()
            workspaces = []
            def fail_security(directory, isu_id):
                workspaces.append(directory.parent)
                raise smoke.SmokeCheckError("certificate generation failed")
            def cleanup():
                self.assertTrue(workspaces[0].is_dir())
                return []
            docker.cleanup.side_effect = cleanup
            with patch.object(smoke, "validate_python_runtime"), patch.object(smoke, "DockerRun", return_value=docker), patch.object(smoke, "prepare_security", side_effect=fail_security), \
                    redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                self.assertEqual(smoke.run(args), 1)
            self.assertFalse(workspaces[0].exists())
            report = json.loads(args.report.read_text())
            self.assertEqual(report["failedStage"], "fresh_his_stand")
            self.assertEqual(report["cleanup"], "passed")

    def test_http_failure_exposes_status_without_response_body(self):
        client = smoke.ApiClient("https://localhost:12345", None)
        error = urllib.error.HTTPError("https://localhost:12345/api/service-auth/token", 500, "error", {}, io.BytesIO(b"secret-response"))
        client.client = Mock()
        client.client.open.side_effect = error
        with self.assertRaises(smoke.SmokeCheckError) as result:
            client.request("POST", "/api/service-auth/token", {"apiKey": "secret-request"})
        self.assertIn("500", str(result.exception))
        self.assertNotIn("secret-request", str(result.exception))
        self.assertNotIn("secret-response", str(result.exception))
        self.assertEqual(client.client.open.call_count, 1)


if __name__ == "__main__":
    unittest.main()
