import argparse
import importlib.util
import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import Mock, patch


SCRIPT = Path(__file__).resolve().parents[1] / "run-application.py"
spec = importlib.util.spec_from_file_location("run_application", SCRIPT)
application = importlib.util.module_from_spec(spec)
spec.loader.exec_module(application)


class ApplicationLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.settings = self.root / "src/Presentation/HospitalInformationSystem.Api/appsettings.Local.json"
        self.settings.parent.mkdir(parents=True)
        self.password = 'Private;Host=localhost;"Quote"$!'
        self.connection = 'Database=hospital;Password="' + self.password.replace('"', '""') + '";Host=localhost;Port=5432'
        self.settings.write_text(json.dumps({"ConnectionStrings": {"Postgres": self.connection}}))
        self.security = self.root / "working-security"
        self.security.mkdir()
        (self.security / "development-security.json").write_text("existing-keys")
        (self.root / ".env").write_text("existing-settings")
        self.arguments = argparse.Namespace(security_directory=str(self.security), start_database=False,
                                            no_build=False, docker="docker")
        self.launcher = Mock()
        self.launcher.run_application.return_value = 0

    def execute(self):
        with patch.object(application, "REPOSITORY", self.root), \
                patch.object(application.importlib.util, "spec_from_file_location"), \
                patch.object(application.importlib.util, "module_from_spec", return_value=self.launcher), \
                redirect_stdout(io.StringIO()) as output:
            code = application.run(self.arguments)
        return code, output.getvalue()

    def test_existing_database_and_keys_are_preserved_and_password_is_not_printed(self):
        observed = {}
        def record_environment(directory, environment, **kwargs):
            self.assertEqual(dict(os.environ), before)
            observed.update(environment)
            return 0
        self.launcher.run_application.side_effect = record_environment
        snapshots = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        with patch.dict(os.environ, {"ConnectionStrings__Postgres": "Host=wrong;Database=demo"}):
            before = dict(os.environ)
            code, output = self.execute()
            self.assertEqual(dict(os.environ), before)
        self.assertEqual(code, 0)
        expected = self.connection.rsplit("Host=localhost", 1)
        self.assertEqual(observed["ConnectionStrings__Postgres"], "Host=host.docker.internal".join(expected))
        self.assertNotIn(self.password, output)
        for path, contents in snapshots.items():
            self.assertEqual(path.read_bytes(), contents)
        self.assertEqual(self.launcher.run_application.call_args.args[0], self.security.resolve())

    def test_application_failure_does_not_change_global_environment(self):
        self.launcher.run_application.side_effect = RuntimeError("Failed to launch")
        before = dict(os.environ)
        with self.assertRaisesRegex(RuntimeError, "Failed to launch"):
            self.execute()
        self.assertEqual(dict(os.environ), before)

    def test_missing_certificates_do_not_generate_or_replace_keys(self):
        (self.security / "development-security.json").unlink()
        with patch.object(application.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "Не найдены рабочие сертификаты"):
                self.execute()
        run.assert_not_called()
        self.launcher.run_application.assert_not_called()

    def test_compose_failure_does_not_start_application(self):
        self.arguments.start_database = True
        with patch.object(application.subprocess, "run", return_value=Mock(returncode=1)) as run:
            with self.assertRaisesRegex(ValueError, "Не удалось запустить PostgreSQL"):
                self.execute()
        arguments = run.call_args.args[0]
        self.assertEqual(arguments[-4:], ["up", "-d", "--wait", "postgres"])
        self.launcher.run_application.assert_not_called()

    def test_nonlocal_host_and_quoted_password_are_not_rewritten(self):
        connection = 'Host=db.example;Password="Host=localhost;P@ss";Database=hospital'
        self.assertEqual(application.docker_connection(connection), connection)

    def test_loopback_aliases_are_rewritten_without_modifying_password(self):
        for value in ["localhost", "127.0.0.1", "::1", "[::1]", '"localhost"', "'localhost'"]:
            with self.subTest(host=value):
                connection = "Server=" + value + ';Password="a;localhost;""b";Database=hospital;'
                self.assertEqual(application.docker_connection(connection),
                                 'Server=host.docker.internal;Password="a;localhost;""b";Database=hospital;')

    def test_broken_settings_do_not_echo_secret(self):
        self.settings.write_text('{"ConnectionStrings":"' + self.password)
        with self.assertRaises(ValueError) as error:
            self.execute()
        self.assertNotIn(self.password, str(error.exception))


if __name__ == "__main__":
    unittest.main()
