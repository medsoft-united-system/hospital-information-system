import argparse
import importlib.util
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


SCRIPT = Path(__file__).resolve().parents[1] / "run-setup-demo.py"
spec = importlib.util.spec_from_file_location("run_setup_demo", SCRIPT)
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)


class SetupDemoTests(unittest.TestCase):
    def setUp(self):
        self.arguments = argparse.Namespace(isu_id="408944", new_certificates=False,
                                            trust_ca=False, no_build=False, docker="docker")
        self.launcher = Mock()
        self.launcher.run_application.return_value = 0

    def execute(self, docker=None, ready=None):
        docker = docker or Mock(side_effect=lambda executable, action, *args, **kwargs:
                               "127.0.0.1:15432" if action == "port" else "ok")
        ready = ready or Mock(return_value=subprocess.CompletedProcess([], 0))
        with patch.object(demo, "docker_command", docker), \
                patch.object(demo, "prepare_security", return_value=(Path("/demo/security"), self.launcher)), \
                patch.object(demo.subprocess, "run", ready):
            return demo.run(self.arguments), docker

    def test_fresh_database_overrides_inherited_connection_without_changing_parent_environment(self):
        observed = {}
        def record_environment(directory, environment, **kwargs):
            self.assertEqual(dict(os.environ), before)
            observed.update(environment)
            return 0
        self.launcher.run_application.side_effect = record_environment
        with patch.dict(os.environ, {"ConnectionStrings__Postgres": "Host=production;Database=important",
                                     "Database__Seed": "true"}):
            before = dict(os.environ)
            code, docker = self.execute()
            self.assertEqual(dict(os.environ), before)
        self.assertEqual(code, 0)
        self.assertIn("Host=host.docker.internal;Port=15432;Database=hospital;", observed["ConnectionStrings__Postgres"])
        self.assertEqual(observed["Database__Seed"], "false")
        self.assertEqual(observed["Database__ApplyMigrations"], "true")
        self.assertEqual(observed["Authentication__Setup__AdministratorUserName"], "admin")
        creation = next(call for call in docker.call_args_list if call.args[1] == "run")
        password = creation.kwargs["environment"]["POSTGRES_PASSWORD"]
        self.assertNotIn(password, " ".join(creation.args))
        self.assertIn("--tmpfs", creation.args)
        self.assertNotIn("--volume", creation.args)
        name = creation.args[creation.args.index("--name") + 1]
        self.assertTrue(name.startswith("medsoft-setup-demo-"))
        self.assertEqual(docker.call_args.args, ("docker", "rm", "--force", name))

    def test_launcher_failure_cleans_only_own_database_and_preserves_environment(self):
        self.launcher.run_application.side_effect = ValueError("Application failed")
        docker = Mock(side_effect=lambda executable, action, *args, **kwargs:
                      "127.0.0.1:15432" if action == "port" else "ok")
        before = dict(os.environ)
        with self.assertRaisesRegex(ValueError, "Application failed"):
            self.execute(docker=docker)
        self.assertEqual(dict(os.environ), before)
        self.assertEqual(docker.call_args.args[1:3], ("rm", "--force"))
        self.assertTrue(docker.call_args.args[3].startswith("medsoft-setup-demo-"))

    def test_keyboard_interrupt_cleans_demo_database(self):
        self.launcher.run_application.side_effect = KeyboardInterrupt
        docker = Mock(side_effect=lambda executable, action, *args, **kwargs:
                      "127.0.0.1:15432" if action == "port" else "ok")
        with self.assertRaises(KeyboardInterrupt):
            self.execute(docker=docker)
        self.assertEqual(docker.call_args.args[1:3], ("rm", "--force"))

    def test_unexpected_port_never_starts_application_and_still_cleans_database(self):
        docker = Mock(return_value="0.0.0.0:5432")
        with self.assertRaisesRegex(ValueError, "неожиданный порт"):
            self.execute(docker=docker)
        self.launcher.run_application.assert_not_called()
        self.assertEqual(docker.call_args.args[1:3], ("rm", "--force"))

    def test_database_readiness_timeout_cleans_database(self):
        docker = Mock(return_value="ok")
        ready = Mock(return_value=subprocess.CompletedProcess([], 1))
        with patch.object(demo.time, "monotonic", side_effect=[0, 91]):
            with self.assertRaisesRegex(ValueError, "90 секунд"):
                self.execute(docker=docker, ready=ready)
        self.launcher.run_application.assert_not_called()
        self.assertEqual(docker.call_args.args[1:3], ("rm", "--force"))

    def test_invalid_isu_does_not_touch_docker_or_certificates(self):
        self.arguments.isu_id = "../existing"
        with patch.object(demo, "docker_command") as docker:
            with self.assertRaisesRegex(ValueError, "ИСУ"):
                demo.run(self.arguments)
        docker.assert_not_called()

    def test_certificate_reissue_uses_new_directory_and_requires_explicit_trust_flag(self):
        self.arguments.new_certificates = True
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            original = home / ".local/share/MedSoft/demo-security/408944"
            original.mkdir(parents=True)
            (original / "working-marker").write_text("preserve")
            fresh = original.with_name("408944-abcdef")
            fresh.mkdir()
            (fresh / "ca").mkdir()
            (fresh / "ca/ca-cert.pem").write_text("public-test-ca")
            (fresh / "development-security.json").write_text('{"setupTokenFile":"/demo/token.txt"}')
            with patch.object(demo.Path, "home", return_value=home), \
                    patch.object(demo.secrets, "token_hex", return_value="abcdef"), \
                    patch.object(demo, "load_launcher", return_value=self.launcher), \
                    patch.object(demo.subprocess, "run") as run:
                directory, _ = demo.prepare_security(self.arguments)
            self.assertEqual(directory, fresh)
            self.assertEqual((original / "working-marker").read_text(), "preserve")
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
