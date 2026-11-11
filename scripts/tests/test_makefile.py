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

    def test_lab3_smoke_passes_lis_checkout_and_report_as_separate_arguments(self):
        source = self.directory / "Go LIS ' source"
        report = self.directory / "report file.json"
        result = self.run_make("lab3-smoke", "PYTHON=" + str(self.executable), "ISU_ID=123456",
                               "LIS_SOURCE=" + str(source), "LAB3_REPORT=" + str(report), "NO_BUILD=1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.probe.read_text()), [
            "scripts/verify-lab3-integration.py", "--isu-id", "123456", "--report", str(report), "--docker", "docker",
            "--lis-source", str(source), "--no-build"])


if __name__ == "__main__":
    unittest.main()
