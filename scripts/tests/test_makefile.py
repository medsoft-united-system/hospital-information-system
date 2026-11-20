import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("make"), "make is required")
class MakefileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="medsoft-make-test-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.probe = self.directory / "arguments.json"
        self.executable = self.directory / "fake executable ' with spaces"
        self.executable.write_text(
            "#!" + sys.executable + "\n"
            "import json, os, sys\n"
            "from pathlib import Path\n"
            "Path(os.environ['MEDSOFT_MAKE_PROBE']).write_text(json.dumps(sys.argv[1:]))\n"
            "Path(os.environ['MEDSOFT_MAKE_PROBE'] + '.env').write_text(json.dumps({key: os.environ.get(key) for key in ('HIS_CA_FILE', 'LAB3_NETWORK')}))\n"
        )
        self.executable.chmod(0o700)

    def run_make(self, *arguments):
        return subprocess.run(
            ["make", "--no-print-directory", *arguments], cwd=REPOSITORY,
            env={**os.environ, "MEDSOFT_MAKE_PROBE": str(self.probe)},
            capture_output=True, text=True, timeout=15, check=False
        )

    def test_default_target_only_prints_help(self):
        result = self.run_make()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("make run", result.stdout)
        self.assertIn("make demo", result.stdout)
        self.assertFalse(self.probe.exists())

    def test_run_passes_existing_security_path_as_one_argument_without_shell_expansion(self):
        path = self.directory / "working security ' $literal"
        result = self.run_make("run", "PYTHON=" + str(self.executable),
                               "SECURITY_DIR=" + str(path).replace("$", "$$"), "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = json.loads(self.probe.read_text())
        self.assertEqual(arguments[0], "scripts/run-application.py")
        self.assertIn("--start-database", arguments)
        self.assertEqual(arguments[arguments.index("--security-directory") + 1], str(path))
        self.assertIn("--no-build", arguments)

    def test_demo_can_reuse_trust_and_forward_isu_without_starting_real_application(self):
        result = self.run_make("demo", "PYTHON=" + str(self.executable),
                               "ISU_ID=123456", "TRUST_CA=0", "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = json.loads(self.probe.read_text())
        self.assertEqual(arguments[0], "scripts/run-setup-demo.py")
        self.assertEqual(arguments[arguments.index("--isu-id") + 1], "123456")
        self.assertNotIn("--trust-ca", arguments)
        self.assertIn("--no-build", arguments)

    def test_demo_default_requests_explicit_certificate_trust(self):
        result = self.run_make("demo", "PYTHON=" + str(self.executable))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--trust-ca", json.loads(self.probe.read_text()))

    def test_database_stop_keeps_container_and_volume_data(self):
        result = self.run_make("db-stop", "DOCKER=" + str(self.executable))
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = json.loads(self.probe.read_text())
        self.assertEqual(arguments[-2:], ["stop", "postgres"])
        self.assertNotIn("down", arguments)
        self.assertNotIn("--volumes", arguments)

    def test_lab3_launch_passes_profile_and_network_to_the_preparer(self):
        source = self.directory / "source security"
        integration = self.directory / "integration security"
        result = self.run_make("lab3-run", "PYTHON=" + str(self.executable), "SECURITY_DIR=" + str(source),
                               "INTEGRATION_SECURITY_DIR=" + str(integration), "LAB3_NETWORK=lab3-private", "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.probe.read_text()), [
            "scripts/prepare-lab3-integration.py", "--run", "--start-database", "--security-directory", str(source),
            "--output-directory", str(integration), "--docker-network", "lab3-private", "--docker", "docker", "--no-build"])

    def test_lab4_prepare_passes_paths_port_and_optional_public_ca_without_shell_expansion(self):
        source = self.directory / "source ' $literal"
        output = self.directory / "client ' $literal"
        ca = self.directory / "T7 ' CA.pem"
        result = self.run_make("lab4-prepare", "PYTHON=" + str(self.executable),
                               "SECURITY_DIR=" + str(source).replace("$", "$$"),
                               "LAB4_SECURITY_DIR=" + str(output).replace("$", "$$"),
                               "HL7_CLIENT_CA=" + str(ca), "MLLP_PORT=12575")
        self.assertEqual(result.returncode, 0, result.stderr)
        args = json.loads(self.probe.read_text())
        self.assertEqual(args[0], "scripts/prepare-lab4-integration.py")
        self.assertEqual(args[args.index("--security-directory") + 1], str(source))
        self.assertEqual(args[args.index("--output-directory") + 1], str(output))
        self.assertEqual(args[args.index("--client-ca") + 1], str(ca))
        self.assertEqual(args[args.index("--mllp-port") + 1], "12575")
        self.assertNotIn("--run", args)

    def test_lab4_run_forwards_tool_paths_and_never_requests_a_database_reset(self):
        result = self.run_make("lab4-run", "PYTHON=" + str(self.executable),
                               "DOTNET=/custom/dotnet", "DOCKER=/custom/docker", "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        args = json.loads(self.probe.read_text())
        self.assertIn("--run", args)
        self.assertIn("--start-database", args)
        self.assertIn("--no-build", args)
        self.assertEqual(args[args.index("--dotnet") + 1], "/custom/dotnet")
        self.assertEqual(args[args.index("--docker") + 1], "/custom/docker")
        self.assertNotIn("--fresh-database", args)

    def test_lab4_check_uses_isolated_launcher_and_tls_upgrade_profile(self):
        result = self.run_make("-n", "lab4-check", "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("scripts/verify-lab4-launcher.py", result.stdout)
        self.assertIn("scripts/verify-lab4-transport.py", result.stdout)
        self.assertIn("--include-upgrade", result.stdout)
        self.assertNotIn("prepare-lab4-integration.py", result.stdout)
        self.assertNotIn("--start-database", result.stdout)

    def test_lab3_smoke_passes_lis_checkout_and_report_as_separate_arguments(self):
        source = self.directory / "Go LIS ' source"
        report = self.directory / "report file.json"
        result = self.run_make("lab3-smoke", "PYTHON=" + str(self.executable), "ISU_ID=123456",
                               "LIS_SOURCE=" + str(source), "LAB3_REPORT=" + str(report), "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.probe.read_text()), [
            "scripts/verify-lab3-integration.py", "--isu-id", "123456", "--report", str(report), "--docker", "docker",
            "--lis-source", str(source), "--no-build"])


    def test_lis_launch_quotes_paths_and_passes_matching_network_and_ca(self):
        repo = self.directory / "LIS ' $literal"
        security = self.directory / "security ' $literal"
        result = self.run_make("lis-up", "PYTHON=" + str(self.executable), "DOCKER=" + str(self.executable),
                               "LIS_REPO=" + str(repo).replace("$", "$$"),
                               "INTEGRATION_SECURITY_DIR=" + str(security).replace("$", "$$"),
                               "LAB3_NETWORK=private-lab3", "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = json.loads(self.probe.read_text())
        self.assertEqual(arguments, ["compose", "--project-directory", str(repo), "-f", str(repo / "compose.yaml"),
                                    "-f", str(REPOSITORY / "compose.lis-integration.yaml"), "up", "-d"])
        environment = json.loads(Path(str(self.probe) + ".env").read_text())
        self.assertEqual(environment, {"HIS_CA_FILE": str(security / "lis-ca-cert.pem"), "LAB3_NETWORK": "private-lab3"})

    def test_lis_operations_keep_data_and_use_same_compose_files(self):
        for target, ending in [("lis-stop", ["stop"]), ("lis-status", ["ps"]),
                               ("lis-logs", ["logs", "--tail", "100", "-f", "lis"])]:
            with self.subTest(target=target):
                result = self.run_make(target, "DOCKER=" + str(self.executable))
                self.assertEqual(result.returncode, 0, result.stderr)
                arguments = json.loads(self.probe.read_text())
                self.assertEqual(arguments[-len(ending):], ending)
                self.assertNotIn("--volumes", arguments)
                self.assertEqual(arguments[0], "compose")

    def test_combined_check_uses_lis_repo_without_losing_literal_dollar(self):
        repo = self.directory / "LIS ' $literal"
        result = self.run_make("lab3-check", "PYTHON=" + str(self.executable),
                               "LIS_REPO=" + str(repo).replace("$", "$$"))
        self.assertEqual(result.returncode, 0, result.stderr)
        arguments = json.loads(self.probe.read_text())
        self.assertEqual(arguments[arguments.index("--lis-source") + 1], str(repo))

    def test_certificates_reuse_existing_manifest(self):
        security = self.directory / "existing security"
        security.mkdir()
        manifest = security / "development-security.json"
        manifest.write_text("existing profile")
        result = self.run_make("certificates", "PYTHON=" + str(self.executable), "SECURITY_DIR=" + str(security))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.probe.exists())
        self.assertEqual(manifest.read_text(), "existing profile")

    def test_new_database_flag_is_explicit(self):
        for value, expected in [("0", []), ("1", ["--fresh-database"])]:
            with self.subTest(value=value):
                result = self.run_make("setup", "PYTHON=" + str(self.executable), "FRESH_DATABASE=" + value)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(self.probe.read_text()), ["scripts/setup-development.py", *expected])


if __name__ == "__main__":
    unittest.main()
