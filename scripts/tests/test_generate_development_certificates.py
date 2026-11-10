import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "generate-development-certificates.py"
spec = importlib.util.spec_from_file_location("generate_development_certificates", SCRIPT)
certificates = importlib.util.module_from_spec(spec)
spec.loader.exec_module(certificates)


class DevelopmentCertificateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            cls.openssl = certificates.find_openssl()
        except ValueError as error:
            raise unittest.SkipTest(str(error)) from error
        cls.temporary = tempfile.TemporaryDirectory(prefix="medsoft-certificates-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name).resolve()
        cls.output = cls.root / "initial"
        cls.result = cls.run_generator("--output-dir", str(cls.output))
        if cls.result.returncode:
            raise AssertionError(cls.result.stderr)
        cls.manifest = json.loads((cls.output / "development-security.json").read_text())

    @classmethod
    def run_generator(cls, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), "--isu-id", "408944", "--openssl",
                               cls.openssl, *arguments], capture_output=True, text=True, timeout=120)

    def openssl_run(self, *arguments):
        return subprocess.run([self.openssl, *map(str, arguments)], capture_output=True,
                              text=True, timeout=30)

    def test_chain_and_all_localhost_addresses_are_verified(self):
        ca = self.output / "ca/ca-cert.pem"
        certificate = self.output / "server/server-cert.pem"
        for option, value in [("-verify_hostname", "localhost"), ("-verify_ip", "127.0.0.1"),
                              ("-verify_ip", "::1")]:
            with self.subTest(value=value):
                result = self.openssl_run("verify", "-CAfile", ca, "-purpose", "sslserver",
                                          option, value, certificate)
                self.assertEqual(result.returncode, 0, result.stderr)
        root = self.openssl_run("verify", "-check_ss_sig", "-CAfile", ca, ca)
        self.assertEqual(root.returncode, 0, root.stderr)

    def test_wrong_hostname_and_untrusted_certificate_are_rejected(self):
        certificate = self.output / "server/server-cert.pem"
        wrong_host = self.openssl_run("verify", "-CAfile", self.output / "ca/ca-cert.pem",
                                      "-verify_hostname", "example.invalid", certificate)
        self.assertNotEqual(wrong_host.returncode, 0)
        untrusted = self.openssl_run("verify", "-no-CAfile", "-no-CApath", certificate)
        self.assertNotEqual(untrusted.returncode, 0)

    def test_issuer_is_exactly_the_ca_subject_with_the_real_isu(self):
        ca_subject = certificates.certificate_subject(self.openssl, self.output / "ca/ca-cert.pem", "subject")
        ca_issuer = certificates.certificate_subject(self.openssl, self.output / "ca/ca-cert.pem", "issuer")
        server_issuer = certificates.certificate_subject(self.openssl, self.output / "server/server-cert.pem", "issuer")
        self.assertEqual(ca_subject, ca_issuer)
        self.assertEqual(ca_subject, server_issuer)
        self.assertEqual(server_issuer["O"], "ITMO")
        self.assertEqual(server_issuer["OU"], "MedSoft 2026/27 408944")

    def test_ca_and_leaf_have_different_constraints(self):
        ca = self.openssl_run("x509", "-in", self.output / "ca/ca-cert.pem", "-noout", "-text")
        leaf = self.openssl_run("x509", "-in", self.output / "server/server-cert.pem", "-noout", "-text")
        self.assertIn("CA:TRUE, pathlen:0", ca.stdout)
        self.assertIn("Certificate Sign, CRL Sign", ca.stdout)
        self.assertIn("CA:FALSE", leaf.stdout)
        self.assertIn("TLS Web Server Authentication", leaf.stdout)
        self.assertNotIn("TLS Web Client Authentication", leaf.stdout)

    def test_tls_and_jwt_have_independent_private_keys(self):
        jwt = self.openssl_run("pkey", "-in", self.output / "jwt/jwt-signing-private.pem", "-pubout")
        leaf = self.openssl_run("x509", "-in", self.output / "server/server-cert.pem", "-noout", "-pubkey")
        ca = self.openssl_run("x509", "-in", self.output / "ca/ca-cert.pem", "-noout", "-pubkey")
        protection = self.openssl_run("x509", "-in", self.output / "data-protection/wrapping-cert.pem", "-noout", "-pubkey")
        self.assertEqual(jwt.returncode, 0, jwt.stderr)
        self.assertEqual(jwt.stdout, (self.output / "jwt/jwt-signing-public.pem").read_text())
        self.assertEqual(len({jwt.stdout, leaf.stdout, ca.stdout, protection.stdout}), 4)

    def test_pfx_contains_leaf_and_ca_and_requires_its_password(self):
        pfx = self.output / "server/server.pfx"
        password = self.output / "server/server-pfx-password.txt"
        parsed = self.openssl_run("pkcs12", "-in", pfx, "-passin", "file:" + str(password), "-nokeys")
        self.assertEqual(parsed.returncode, 0, parsed.stderr)
        self.assertEqual(parsed.stdout.count("-----BEGIN CERTIFICATE-----"), 2)
        algorithms = self.openssl_run("pkcs12", "-in", pfx, "-passin", "file:" + str(password), "-info", "-noout")
        self.assertEqual(algorithms.returncode, 0, algorithms.stderr)
        self.assertIn("AES-256-CBC", algorithms.stderr)
        self.assertIn("Iteration 100000", algorithms.stderr)
        invalid_password = self.openssl_run("pkcs12", "-in", pfx, "-passin", "pass:wrong-password", "-nokeys")
        self.assertNotEqual(invalid_password.returncode, 0)

    @unittest.skipIf(os.name == "nt", "POSIX file modes")
    def test_all_material_is_private_and_passwords_are_not_printed(self):
        for path in self.output.rglob("*"):
            with self.subTest(path=path.name):
                self.assertEqual(path.stat().st_mode & 0o777, 0o700 if path.is_dir() else 0o600)
                if path.name.endswith("password.txt"):
                    self.assertNotIn(path.read_text().strip(), self.result.stdout + self.result.stderr)
        token = Path(self.manifest["setupTokenFile"]).read_text().strip()
        self.assertGreaterEqual(len(token), 43)
        self.assertNotIn(token, self.result.stdout + self.result.stderr)
        self.assertEqual(Path(self.manifest["dataProtectionKeyDirectory"]).stat().st_mode & 0o777, 0o700)
        self.assertNotIn("PRIVATE KEY", self.result.stdout + self.result.stderr)

    def test_repeat_never_overwrites_existing_keys(self):
        before = {str(path.relative_to(self.output)): path.read_bytes()
                  for path in self.output.rglob("*") if path.is_file()}
        result = self.run_generator("--output-dir", str(self.output))
        self.assertNotEqual(result.returncode, 0)
        after = {str(path.relative_to(self.output)): path.read_bytes()
                 for path in self.output.rglob("*") if path.is_file()}
        self.assertEqual(before, after)

    def test_renewal_keeps_the_ca_and_does_not_rotate_jwt(self):
        output = self.root / "renewed"
        protection_pfx = Path(self.manifest["dataProtectionPfxPath"])
        protection_before = protection_pfx.read_bytes()
        stored_key = Path(self.manifest["dataProtectionKeyDirectory"]) / "existing-key.xml"
        stored_key.write_text("persisted-keyring-test-placeholder")
        stored_key.chmod(0o600)
        result = self.run_generator("--output-dir", str(output), "--ca-directory", str(self.output / "ca"),
                                    "--data-protection-directory", str(self.output / "data-protection"),
                                    "--jwt-directory", str(self.output / "jwt"),
                                    "--setup-token-file", self.manifest["setupTokenFile"],
                                    "--dns-name", "his-api", "--dns-name", "medsoft.local", "--ip-address", "192.0.2.10")
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = json.loads((output / "development-security.json").read_text())
        self.assertEqual(manifest["caCertificatePath"], str(self.output / "ca/ca-cert.pem"))
        self.assertEqual(manifest["jwtPrivateKeyPath"], self.manifest["jwtPrivateKeyPath"])
        self.assertEqual(manifest["jwtPublicKeyPath"], self.manifest["jwtPublicKeyPath"])
        self.assertFalse((output / "jwt").exists())
        self.assertEqual(manifest["dataProtectionPfxPath"], self.manifest["dataProtectionPfxPath"])
        self.assertEqual(manifest["dataProtectionKeyDirectory"], self.manifest["dataProtectionKeyDirectory"])
        self.assertFalse((output / "data-protection").exists())
        self.assertEqual(protection_pfx.read_bytes(), protection_before)
        self.assertEqual(stored_key.read_text(), "persisted-keyring-test-placeholder")
        self.assertEqual(Path(manifest["setupTokenFile"]).read_bytes(), Path(self.manifest["setupTokenFile"]).read_bytes())
        self.assertNotEqual(manifest["setupTokenFile"], self.manifest["setupTokenFile"])
        verified = self.openssl_run("verify", "-CAfile", self.output / "ca/ca-cert.pem", "-purpose", "sslserver",
                                    "-verify_hostname", "medsoft.local", output / "server/server-cert.pem")
        self.assertEqual(verified.returncode, 0, verified.stderr)
        alias = self.openssl_run("verify", "-CAfile", self.output / "ca/ca-cert.pem", "-purpose", "sslserver",
                                 "-verify_hostname", "his-api", output / "server/server-cert.pem")
        self.assertEqual(alias.returncode, 0, alias.stderr)
        self.assertNotEqual((output / "server/server-cert.pem").read_bytes(),
                            (self.output / "server/server-cert.pem").read_bytes())

    def test_reusing_mismatched_jwt_keys_is_rejected_before_issuing_certificates(self):
        directory = self.root / "mismatched-jwt"
        directory.mkdir(mode=0o700)
        private = directory / "jwt-signing-private.pem"
        public = directory / "jwt-signing-public.pem"
        private.write_bytes(Path(self.manifest["jwtPrivateKeyPath"]).read_bytes())
        public.write_text(self.openssl_run("x509", "-in", self.output / "server/server-cert.pem", "-noout", "-pubkey").stdout)
        private.chmod(0o600)
        public.chmod(0o600)
        destination = self.root / "mismatched-output"

        result = self.run_generator("--output-dir", str(destination), "--jwt-directory", str(directory))

        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(destination.exists())

    def test_lab3_preparation_preserves_source_and_reuses_verified_leaf(self):
        output = self.root / "lab3-profile"
        launcher = SCRIPT.with_name("prepare-lab3-integration.py")
        arguments = [sys.executable, str(launcher), "--security-directory", str(self.output),
                     "--output-directory", str(output), "--openssl", self.openssl,
                     "--docker", str(self.root / "no-docker-needed")]
        source_before = {path: path.read_bytes() for path in self.output.rglob("*") if path.is_file()}
        first = subprocess.run(arguments, capture_output=True, text=True, timeout=120)
        self.assertEqual(first.returncode, 0, first.stderr)
        manifest = json.loads((output / "development-security.json").read_text())
        for field in ("caCertificatePath", "jwtPrivateKeyPath", "jwtPublicKeyPath", "dataProtectionPfxPath",
                      "dataProtectionPfxPasswordFile", "dataProtectionKeyDirectory"):
            self.assertEqual(manifest[field], self.manifest[field])
        token = Path(self.manifest["setupTokenFile"])
        self.assertEqual(Path(manifest["setupTokenFile"]).read_bytes(), source_before[token])
        self.assertNotIn(token.read_text().strip(), first.stdout + first.stderr)
        self.assertIn("https://his-api:5201", first.stdout)
        exported_ca = output / "lis-ca-cert.pem"
        self.assertIn(str(exported_ca), first.stdout)
        self.assertEqual(exported_ca.read_bytes(), Path(manifest["caCertificatePath"]).read_bytes())
        if os.name != "nt":
            self.assertEqual(exported_ca.stat().st_mode & 0o777, 0o644)
        prepared_before = {path: path.read_bytes() for path in output.rglob("*") if path.is_file()}
        exported_ca.unlink()
        second = subprocess.run(arguments, capture_output=True, text=True, timeout=120)
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual({path: path.read_bytes() for path in output.rglob("*") if path.is_file()}, prepared_before)
        self.assertEqual({path: path.read_bytes() for path in self.output.rglob("*") if path.is_file()}, source_before)
        verify = self.openssl_run("verify", "-CAfile", exported_ca, "-purpose", "sslserver",
                                  "-verify_hostname", "his-api", manifest["serverCertificatePath"])
        self.assertEqual(verify.returncode, 0, verify.stderr)

    def test_invalid_preserved_setup_token_is_rejected_before_creating_output(self):
        for index, value in enumerate([b"short", b"a" * 32 + b"\nsecond", b"a" * 32 + b"\xc2\x85", b"\xff" * 32]):
            with self.subTest(index=index):
                token = self.root / ("invalid-owner-" + str(index))
                token.write_bytes(value)
                token.chmod(0o600)
                destination = self.root / ("invalid-owner-output-" + str(index))
                result = self.run_generator("--output-dir", str(destination), "--setup-token-file", str(token))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(destination.exists())
                decoded = value.decode("utf-8", errors="ignore")
                if decoded:
                    self.assertNotIn(decoded, result.stdout + result.stderr)

    def test_invalid_subject_san_or_lifetime_creates_no_files(self):
        for index, extra in enumerate([("--isu-id", "NNN"), ("--isu-id", "408944\nCN=attacker"),
                                        ("--dns-name", "localhost\nIP.4=10.0.0.1"),
                                        ("--dns-name", "*.example.org"), ("--ip-address", "not-an-ip"),
                                        ("--valid-days", "0"), ("--valid-days", "826")]):
            with self.subTest(arguments=extra):
                output = self.root / ("invalid-" + str(index))
                result = self.run_generator("--output-dir", str(output), *extra)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(output.exists())

    def test_output_inside_git_repository_is_rejected(self):
        output = SCRIPT.parent.parent / "certificate-test-output-must-not-exist"
        result = self.run_generator("--output-dir", str(output))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists())

    def test_renewal_with_wrong_isu_is_rejected(self):
        output = self.root / "wrong-owner"
        result = self.run_generator("--output-dir", str(output), "--ca-directory", str(self.output / "ca"),
                                    "--isu-id", "999999")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists())

    @unittest.skipIf(os.name == "nt", "POSIX symlinks")
    def test_output_symlink_is_rejected_without_changing_its_target(self):
        target = self.root / "symlink-target"
        target.mkdir()
        link = self.root / "symlink-output"
        link.symlink_to(target, target_is_directory=True)
        result = self.run_generator("--output-dir", str(link / "generated"))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(list(target.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
