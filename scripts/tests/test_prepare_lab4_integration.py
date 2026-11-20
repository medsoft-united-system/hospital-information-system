import argparse
import importlib.util
import io
import ssl
from pathlib import Path
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import Mock, patch


REPOSITORY = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("lab4_prepare_tests", REPOSITORY / "scripts/prepare-lab4-integration.py")
preparer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preparer)
spec = importlib.util.spec_from_file_location("lab4_launcher_tests", REPOSITORY / "scripts/verify-lab4-launcher.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class Lab4PreparationTests(unittest.TestCase):
    def arguments(self, **overrides):
        return argparse.Namespace(**{
            "security_directory": "/private/tmp/source-security",
            "integration_security_directory": "/private/tmp/https-profile",
            "output_directory": "/private/tmp/hl7-client",
            "client_ca": None, "openssl": None, "mllp_port": 2575,
            "docker_network": "medsoft-lab3", "run": False, "start_database": False,
            "no_build": False, "docker": "docker", "dotnet": "dotnet", **overrides})

    def profile(self):
        return {"httpsDirectory": "/private/tmp/https-profile",
                "serverCaCertificatePath": "/private/tmp/server-ca.pem",
                "serverPfxPath": "/private/tmp/server.pfx",
                "serverPfxPasswordFile": "/private/tmp/pfx-password.txt",
                "clientCaCertificatePath": "/private/tmp/public-client-ca.pem",
                "clientCertificatePath": "/private/tmp/client.pem",
                "clientKeyPath": "/private/tmp/client-key.pem"}

    def test_preparation_reuses_lab3_security_and_offline_client_issuer(self):
        lab3, launcher, certificates, issuer = Mock(), Mock(), Mock(), Mock()
        lab3.prepare_security.return_value = (Path("/private/tmp/https-profile"), Path("/private/tmp/lis-ca.pem"))
        lab3.load_manifest.side_effect = [
            {"serverPfxPath": "/private/tmp/server.pfx", "serverPfxPasswordFile": "/private/tmp/pfx-password.txt",
             "caCertificatePath": "/private/tmp/server-ca.pem"},
            {"caCertificatePath": "/private/tmp/source-security/ca/ca-cert.pem"}]
        certificates.find_openssl.return_value = "/openssl"
        issuer.prepare_client.return_value = {
            "caCertificatePath": "/private/tmp/public-client-ca.pem",
            "clientCertificatePath": "/private/tmp/client.pem", "clientKeyPath": "/private/tmp/client-key.pem"}
        with patch.object(preparer, "load_tool", side_effect=[lab3, launcher, certificates, issuer]):
            profile = preparer.prepare_security(self.arguments())
        self.assertEqual(profile, self.profile())
        original = lab3.prepare_security.call_args.args[0]
        self.assertEqual(original.security_directory, "/private/tmp/source-security")
        self.assertEqual(original.output_directory, "/private/tmp/https-profile")
        issuer.prepare_client.assert_called_once_with(
            Path("/private/tmp/source-security/ca"), Path("/private/tmp/hl7-client"), openssl="/openssl")

    def test_external_client_ca_does_not_generate_a_private_client_key(self):
        lab3, launcher, certificates = Mock(), Mock(), Mock()
        lab3.prepare_security.return_value = (Path("/private/tmp/https-profile"), Path("/private/tmp/lis-ca.pem"))
        lab3.load_manifest.return_value = {"serverPfxPath": "server.pfx", "serverPfxPasswordFile": "password.txt", "caCertificatePath": "server-ca.pem"}
        with patch.object(preparer, "load_tool", side_effect=[lab3, launcher, certificates]) as load, \
                patch.object(preparer, "validate_client_ca", return_value=Path("/private/tmp/t7-ca.pem")):
            result = preparer.prepare_security(self.arguments(client_ca="/private/tmp/t7-ca.pem"))
        self.assertEqual(load.call_count, 3)
        self.assertEqual(result["clientCaCertificatePath"], "/private/tmp/t7-ca.pem")
        self.assertIsNone(result["clientKeyPath"])
        self.assertIsNone(result["clientCertificatePath"])

    def test_invalid_port_fails_before_keys_or_docker_are_touched(self):
        for value in [0, 65536, 5101, 5201, "-1", "1;touch /tmp/unsafe", "１２３", True]:
            with self.subTest(value=value), patch.object(preparer, "load_tool") as load:
                with self.assertRaises(ValueError):
                    preparer.prepare_security(self.arguments(mllp_port=value))
                load.assert_not_called()

    def test_explicit_environment_preserves_lis_database_and_keys_without_mutating_input(self):
        original = {"ConnectionStrings__Postgres": "Host=test;Password=NotPrinted",
                    "Fhir__PublicBaseUrl": "https://his-api:5201/fhir",
                    "Authentication__Jwt__PrivateKeyPath": "/working/jwt.pem",
                    "Security__DataProtection__KeyDirectory": "/working/keyring",
                    "Database__Seed": "true"}
        with patch.object(preparer, "load_tool") as load:
            result = preparer.application_environment(self.arguments(), self.profile(), base_environment=original)
        load.return_value.database_environment.assert_not_called()
        for key in original:
            if key != "Database__Seed":
                self.assertEqual(result[key], original[key])
        self.assertEqual(original["Database__Seed"], "true")
        self.assertEqual(result["Database__ApplyMigrations"], "true")
        self.assertEqual(result["Database__Seed"], "false")
        self.assertEqual(result["Hl7Import__ListenAddress"], "0.0.0.0")
        self.assertEqual(result["Hl7Import__Port"], "2575")
        self.assertEqual(result["Hl7Import__ServerCertificate__PasswordFile"], self.profile()["serverPfxPasswordFile"])

    def test_prepare_only_does_not_start_database_or_application(self):
        output = io.StringIO()
        with patch.object(preparer, "prepare_security", return_value=self.profile()), \
                patch.object(preparer, "application_environment") as environment, \
                patch.object(preparer.subprocess, "run") as process, redirect_stdout(output):
            self.assertEqual(preparer.run(self.arguments()), 0)
        environment.assert_not_called()
        process.assert_not_called()
        self.assertIn("https://localhost:5201", output.getvalue())
        self.assertIn("localhost:2575", output.getvalue())
        self.assertNotIn("https://his-api", output.getvalue())

    def test_database_launch_uses_up_wait_and_preserves_volume(self):
        launcher = Mock()
        launcher.run_application.return_value = 0
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".env").write_text("ignored-fixture")
            with patch.object(preparer, "REPOSITORY", root), \
                    patch.object(preparer, "prepare_security", return_value=self.profile()), \
                    patch.object(preparer, "application_environment", return_value={"fixture": "value"}), \
                    patch.object(preparer, "load_tool", return_value=launcher), \
                    patch.object(preparer.subprocess, "run", return_value=Mock(returncode=0)) as run, \
                    redirect_stdout(io.StringIO()):
                self.assertEqual(preparer.run(self.arguments(run=True, start_database=True, no_build=True)), 0)
            command = run.call_args.args[0]
            self.assertEqual(command[-4:], ["up", "-d", "--wait", "postgres"])
            self.assertNotIn("down", command)
            self.assertNotIn("--volumes", command)
            launcher.run_application.assert_called_once_with(
                "/private/tmp/https-profile", {"fixture": "value"}, no_build=True, docker="docker",
                dotnet="dotnet", docker_network="medsoft-lab3", network_alias="his-api")

    def test_failed_database_start_never_starts_api(self):
        launcher = Mock()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".env").write_text("ignored-fixture")
            with patch.object(preparer, "REPOSITORY", root), \
                    patch.object(preparer, "prepare_security", return_value=self.profile()), \
                    patch.object(preparer, "application_environment", return_value={}), \
                    patch.object(preparer, "load_tool", return_value=launcher), \
                    patch.object(preparer.subprocess, "run", return_value=Mock(returncode=1)), \
                    redirect_stdout(io.StringIO()):
                with self.assertRaises(ValueError):
                    preparer.run(self.arguments(run=True, start_database=True))
        launcher.run_application.assert_not_called()


class Lab4LauncherIsolationTests(unittest.TestCase):
    def test_probe_uses_c4_fixture_and_only_access_token_in_uac(self):
        frame = smoke.frame_with_token("test.jwt.access")
        self.assertTrue(frame.startswith(b"\x0bMSH|"))
        self.assertTrue(frame.endswith(b"\x1c\r"))
        self.assertIn(b"NMD^N02^NMD_N02", frame)
        self.assertIn(b"dGVzdC5qd3QuYWNjZXNz", frame)
        self.assertNotIn(b"lab4-fixture-access-token-not-valid", frame)

    def test_isolated_launch_uses_explicit_database_and_ephemeral_ports(self):
        docker = Mock(prefix="medsoft-check", network="test-network", run_id="test-run")
        docker.port.side_effect = [50001, 50002]
        prep, launcher = Mock(), Mock()
        prep.application_environment.return_value = {"fixture": "clean"}
        launcher.certificate_environment.return_value = {"fixture": "certified"}
        launcher.api_configuration.return_value = (
            ["run", "--rm", "--name", "medsoft-check-his-api", "--publish", "127.0.0.1:5201:5201",
             "--publish", "127.0.0.1:2575:2575", "old-image"], {"fixture": "container"})
        isolated = Mock()
        isolated.clean_environment.return_value = {"PATH": "/bin"}
        arguments = argparse.Namespace(mllp_port=2575)
        with patch.object(smoke, "load_tool", side_effect=[prep, launcher]):
            _, origin, port = smoke.start_api(isolated, docker, arguments,
                {"httpsDirectory": "/private/tmp/test-profile"}, "Host=his-db;Password=IsolatedOnly")
        command = docker.command.call_args.args
        self.assertIn("127.0.0.1::5201", command)
        self.assertIn("127.0.0.1::2575", command)
        self.assertNotIn("127.0.0.1:5201:5201", command)
        self.assertEqual(command[-1], smoke.API_IMAGE)
        self.assertNotIn("IsolatedOnly", str(command))
        base = prep.application_environment.call_args.kwargs["base_environment"]
        self.assertEqual(base["ConnectionStrings__Postgres"], "Host=his-db;Password=IsolatedOnly")
        self.assertEqual(origin, "https://localhost:50001")
        self.assertEqual(port, 50002)

    def test_no_certificate_refusal_is_followed_by_a_healthy_authenticated_probe(self):
        without_certificate, with_certificate = object(), object()
        with patch.object(smoke, "exchange", side_effect=[ssl.SSLError("certificate required"), ("AA", "CONNECTED")]) as exchange:
            smoke.verify_client_certificate_requirement(2575, without_certificate, with_certificate, "synthetic.jwt")
        self.assertEqual(exchange.call_args_list[0].args, (2575, without_certificate, "synthetic.jwt"))
        self.assertEqual(exchange.call_args_list[1].args, (2575, with_certificate, "synthetic.jwt"))

    def test_unavailable_listener_is_not_reported_as_a_successful_mtls_refusal(self):
        with patch.object(smoke, "exchange", side_effect=[ConnectionRefusedError(), ConnectionRefusedError()]) as exchange:
            with self.assertRaises(ConnectionRefusedError):
                smoke.verify_client_certificate_requirement(2575, object(), object(), "synthetic.jwt")
        self.assertEqual(exchange.call_count, 2)

    def test_refused_authenticated_probe_after_no_certificate_is_not_a_success(self):
        with patch.object(smoke, "exchange", side_effect=[ConnectionError(), ("AR", "AUTH")]):
            with self.assertRaisesRegex(ValueError, "not healthy"):
                smoke.verify_client_certificate_requirement(2575, object(), object(), "synthetic.jwt")

    def test_accepting_a_probe_without_certificate_fails_the_check(self):
        with patch.object(smoke, "exchange", return_value=("AA", "CONNECTED")) as exchange:
            with self.assertRaisesRegex(ValueError, "without a client certificate"):
                smoke.verify_client_certificate_requirement(2575, object(), object(), "synthetic.jwt")
        self.assertEqual(exchange.call_count, 1)

    def test_snapshot_detects_key_replacement(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            key = root / "test.key"
            key.write_text("old")
            before = smoke.snapshot(root)
            key.write_text("new")
            self.assertNotEqual(before, smoke.snapshot(root))


if __name__ == "__main__":
    unittest.main()
