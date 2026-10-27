import contextlib
import importlib.util
import io
import socket
import ssl
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).resolve().parents[1]


def load_script(name, filename):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


certificates = load_script("live_tls_certificates", "generate-development-certificates.py")
verifier = load_script("live_tls_verifier", "verify-lab2-tls.py")


class LiveTlsVerificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not ssl.HAS_TLSv1_3:
            raise unittest.SkipTest("Python SSL does not support TLS 1.3.")
        try:
            cls.openssl = certificates.find_openssl()
        except ValueError as error:
            raise unittest.SkipTest(str(error)) from error
        cls.temporary = tempfile.TemporaryDirectory(prefix="medsoft-live-tls-")
        cls.addClassCleanup(cls.temporary.cleanup)
        root = Path(cls.temporary.name)
        cls.key = root / "synthetic-server.key"
        cls.certificate = root / "synthetic-server.pem"
        config = root / "synthetic-server.cnf"
        config.write_text("\n".join([
            "[req]", "prompt=no", "distinguished_name=subject", "x509_extensions=extensions",
            "[subject]", "O=ITMO", "OU=MedSoft 2026/27 123456", "CN=localhost",
            "[extensions]", "subjectAltName=DNS:localhost,IP:127.0.0.1",
            "basicConstraints=critical,CA:TRUE", "keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign",
            "extendedKeyUsage=serverAuth", ""]))
        result = subprocess.run([cls.openssl, "req", "-new", "-x509", "-newkey", "rsa:3072", "-nodes",
                                 "-days", "1", "-config", str(config), "-keyout", str(cls.key),
                                 "-out", str(cls.certificate)], capture_output=True, text=True, timeout=30)
        if result.returncode:
            raise AssertionError("Cannot create synthetic certificate: " + result.stderr)
        cls.key.chmod(0o600)

    @contextlib.contextmanager
    def endpoint(self, minimum):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = minimum
        context.maximum_version = ssl.TLSVersion.TLSv1_3
        context.load_cert_chain(self.certificate, self.key)
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.bind(("127.0.0.1", 0))
            listener.listen(5)
        except OSError:
            listener.close()
            raise
        listener.settimeout(0.1)
        port = listener.getsockname()[1]
        stopped = threading.Event()

        def serve():
            while not stopped.is_set():
                try:
                    client, _ = listener.accept()
                except socket.timeout:
                    continue
                except OSError:
                    break
                with client:
                    client.settimeout(2)
                    try:
                        with context.wrap_socket(client, server_side=True) as secure:
                            secure.unwrap().close()
                    except OSError:
                        pass

        thread = threading.Thread(target=serve, daemon=True)
        thread.start()
        try:
            yield port
        finally:
            stopped.set()
            listener.close()
            thread.join(timeout=3)
            self.assertFalse(thread.is_alive(), "Temporary TLS server did not stop.")

    def verify(self, port):
        output, errors = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            code = verifier.main(["--host", "127.0.0.1", "--port", str(port), "--ca", str(self.certificate),
                                  "--isu-id", "123456", "--openssl", self.openssl, "--timeout", "5"])
        return code, output.getvalue(), errors.getvalue()

    def test_real_tls13_only_server_passes_the_complete_verifier(self):
        with self.endpoint(ssl.TLSVersion.TLSv1_3) as port:
            code, output, errors = self.verify(port)
        self.assertEqual(code, 0, errors)
        self.assertIn("Сервер отклонил TLS 1.2", output)

    def test_real_server_accepting_tls12_fails_the_complete_verifier(self):
        with self.endpoint(ssl.TLSVersion.TLSv1_2) as port:
            code, _, errors = self.verify(port)
        self.assertEqual(code, 1, errors)
        self.assertIn("TLS 1.2", errors)


if __name__ == "__main__":
    unittest.main()
