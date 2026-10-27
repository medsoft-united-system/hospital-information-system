import contextlib
import importlib.util
import io
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-lab2-tls.py"
spec = importlib.util.spec_from_file_location("verify_lab2_tls", SCRIPT)
verify_lab2_tls = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_lab2_tls)

Completed = verify_lab2_tls.Completed

HANDSHAKE_OK = Completed(
    0,
    "CONNECTED\n-----BEGIN CERTIFICATE-----\nMIIBserver\n-----END CERTIFICATE-----\n"
    "    Protocol  : TLSv1.3\n    Verify return code: 0 (ok)\n",
    "",
)
ISSUER_OK = "issuer=OU=MedSoft 2026/27 123456,O=ITMO,CN=medsoft.local\n"


def connect_value(args):
    return args[args.index("-connect") + 1]


class FakeRunner:
    def __init__(self):
        self.help_text = "usage: s_client ... -tls1_2 -tls1_3 -no-CAfile -no-CApath -no-CAstore ...\n"
        self.handshake = HANDSHAKE_OK
        self.issuer = ISSUER_OK
        self.checkend = Completed(0, "", "")
        self.tls12 = Completed(1, "", "tlsv1 alert protocol version")
        self.timeouts = set()
        self.calls = []

    def __call__(self, args, timeout, stdin=""):
        self.calls.append(args)
        command = args[1]
        if command == "s_client" and "-help" in args:
            return Completed(1, "", self.help_text)
        if command == "s_client" and "-tls1_3" in args:
            if "tls13" in self.timeouts:
                raise subprocess.TimeoutExpired(args, timeout)
            return self.handshake
        if command == "s_client" and "-tls1_2" in args:
            if "tls12" in self.timeouts:
                raise subprocess.TimeoutExpired(args, timeout)
            return self.tls12
        if command == "x509" and "-issuer" in args:
            return Completed(0, self.issuer, "")
        if command == "x509" and "-checkend" in args:
            return self.checkend
        raise AssertionError(f"Неожиданный вызов: {args}")


class VerifyTlsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="medsoft-tls-test-")
        self.addCleanup(self.directory.cleanup)
        self.ca = Path(self.directory.name) / "root-ca.crt"
        self.ca.write_text("-----BEGIN CERTIFICATE-----\nca\n-----END CERTIFICATE-----\n", encoding="utf-8")

    def run_main(self, runner, host="localhost", isu="123456", extra=None):
        argv = ["--host", host, "--port", "5201", "--ca", str(self.ca), "--isu-id", isu]
        if extra:
            argv += extra
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = verify_lab2_tls.main(argv, runner=runner)
        return code, stdout.getvalue(), stderr.getvalue()

    def test_a_compliant_stand_passes(self):
        code, out, _ = self.run_main(FakeRunner())
        self.assertEqual(code, 0)
        self.assertIn("TLS 1.3", out)
        self.assertIn("Сервер отклонил TLS 1.2", out)

    def test_tool_without_tls13_is_reported_as_tool_error(self):
        runner = FakeRunner()
        runner.help_text = "usage: s_client ... -tls1_2 -no-CAfile ...\n"
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 2)
        self.assertIn("TLS 1.3", err)

    def test_tool_without_trust_isolation_flags_is_rejected(self):
        runner = FakeRunner()
        runner.help_text = "usage: s_client ... -tls1_2 -tls1_3 ...\n"
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 2)
        self.assertIn("системное доверие", err)

    def test_handshake_disables_system_trust(self):
        runner = FakeRunner()
        code, _, _ = self.run_main(runner)
        self.assertEqual(code, 0)
        handshake = next(call for call in runner.calls
                         if call[1] == "s_client" and "-tls1_3" in call)
        for flag in ("-no-CAfile", "-no-CApath", "-no-CAstore"):
            self.assertIn(flag, handshake)

    def test_failed_handshake_is_a_stand_failure(self):
        runner = FakeRunner()
        runner.handshake = Completed(1, "", "verify error:num=20")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("handshake", err)

    def test_wrong_issuer_fails(self):
        runner = FakeRunner()
        runner.issuer = "issuer=OU=MedSoft 2026/27 999999,O=ITMO,CN=medsoft.local\n"
        code, _, err = self.run_main(runner, isu="123456")
        self.assertEqual(code, 1)
        self.assertIn("MedSoft 2026/27 123456", err)

    def test_missing_itmo_organisation_fails(self):
        runner = FakeRunner()
        runner.issuer = "issuer=OU=MedSoft 2026/27 123456,O=OTHER,CN=medsoft.local\n"
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("O=ITMO", err)

    def test_expired_certificate_fails(self):
        runner = FakeRunner()
        runner.checkend = Completed(1, "", "")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("истёк", err)

    def test_issuer_with_extra_organisation_suffix_fails(self):
        runner = FakeRunner()
        runner.issuer = "issuer=OU=MedSoft 2026/27 1234567,O=ITMO-Fake,CN=medsoft.local\n"
        code, _, err = self.run_main(runner, isu="123456")
        self.assertEqual(code, 1)
        self.assertIn("O=ITMO", err)

    def test_issuer_with_isu_common_prefix_fails(self):
        runner = FakeRunner()
        runner.issuer = "issuer=OU=MedSoft 2026/27 1234567,O=ITMO,CN=medsoft.local\n"
        code, _, err = self.run_main(runner, isu="123456")
        self.assertEqual(code, 1)
        self.assertIn("MedSoft 2026/27 123456", err)

    def test_server_that_accepts_tls12_fails(self):
        runner = FakeRunner()
        runner.tls12 = Completed(0, "    Protocol  : TLSv1.2\n", "")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("TLS 1.2", err)

    def test_tls12_negotiated_then_eof_fails(self):
        runner = FakeRunner()
        runner.tls12 = Completed(1, "    Protocol  : TLSv1.2\n", "0000 - unexpected eof while reading\n")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("TLS 1.2", err)

    def test_unrelated_error_is_not_a_tls12_rejection(self):
        runner = FakeRunner()
        runner.tls12 = Completed(1, "", "write:errno=104\n")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("не подтверждён", err)

    def test_connectivity_error_does_not_confirm_tls12_rejection(self):
        runner = FakeRunner()
        runner.tls12 = Completed(1, "", "connect:errno=61 Connection refused")
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("соединения", err)

    def test_tls12_timeout_is_not_a_confirmation(self):
        runner = FakeRunner()
        runner.timeouts = {"tls12"}
        code, _, err = self.run_main(runner)
        self.assertEqual(code, 1)
        self.assertIn("Тайм-аут", err)

    def test_missing_ca_file_is_an_argument_error(self):
        runner = FakeRunner()
        stdout, stderr = io.StringIO(), io.StringIO()
        argv = ["--host", "localhost", "--port", "5201", "--ca", str(self.ca) + ".missing", "--isu-id", "123456"]
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = verify_lab2_tls.main(argv, runner=runner)
        self.assertEqual(code, 2)

    def test_ip_host_uses_ip_verification_without_sni(self):
        runner = FakeRunner()
        code, _, _ = self.run_main(runner, host="127.0.0.1")
        self.assertEqual(code, 0)
        handshake = next(call for call in runner.calls
                         if call[1] == "s_client" and "-tls1_3" in call)
        self.assertIn("-verify_ip", handshake)
        self.assertNotIn("-servername", handshake)
        self.assertIn("127.0.0.1:5201", connect_value(handshake))

    def test_ipv4_connect_target_has_no_brackets(self):
        self.assertEqual(verify_lab2_tls.connect_target("127.0.0.1", 5201), "127.0.0.1:5201")

    def test_ipv6_connect_target_is_bracketed(self):
        self.assertEqual(verify_lab2_tls.connect_target("::1", 5201), "[::1]:5201")

    def test_hostname_connect_target_is_plain(self):
        self.assertEqual(verify_lab2_tls.connect_target("medsoft.local", 5201), "medsoft.local:5201")

    def test_ipv6_host_is_bracketed_in_both_calls(self):
        runner = FakeRunner()
        code, _, _ = self.run_main(runner, host="::1")
        self.assertEqual(code, 0)
        for call in runner.calls:
            if "-connect" in call:
                self.assertEqual("[::1]:5201", connect_value(call))


if __name__ == "__main__":
    unittest.main()
