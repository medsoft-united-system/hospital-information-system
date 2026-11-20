import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "prepare-hl7-client-certificate.py"
spec = importlib.util.spec_from_file_location("prepare_hl7_client_certificate", SCRIPT)
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


class Hl7ClientCertificateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            cls.openssl = client.certificates.find_openssl()
        except ValueError as error:
            raise unittest.SkipTest(str(error)) from error
        cls.temporary = tempfile.TemporaryDirectory(prefix="medsoft-hl7-client-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name).resolve()
        cls.ca = cls.create_ca("issuer")
        cls.other_ca = cls.create_ca("other-issuer")
        cls.initial = cls.root / "initial client ' with spaces"
        cls.manifest = client.prepare_client(cls.ca, cls.initial, cls.openssl)

    @classmethod
    def command(cls, *args):
        return client.certificates.openssl_command(cls.openssl, *args)

    @classmethod
    def create_ca(cls, name):
        directory = cls.root / name
        directory.mkdir(mode=0o700)
        client.certificates.private_write(directory / "ca-key-password.txt", "Synthetic-CA.Password-9!\n")
        client.certificates.key_pair(cls.openssl, directory / "ca-key.pem", directory / "ca-key-password.txt")
        config = directory / "ca.cnf"
        client.certificates.private_write(config, "\n".join([
            "[req]", "prompt=no", "distinguished_name=subject", "x509_extensions=extensions", "[subject]",
            "CN=" + name, "[extensions]", "basicConstraints=critical,CA:TRUE,pathlen:0",
            "keyUsage=critical,keyCertSign,cRLSign", "subjectKeyIdentifier=hash", ""]))
        cls.command("req", "-new", "-x509", "-days", "1000", "-key", directory / "ca-key.pem",
                    "-passin", "file:" + str(directory / "ca-key-password.txt"), "-config", config,
                    "-out", directory / "ca-cert.pem")
        (directory / "ca-cert.pem").chmod(0o600)
        return directory

    def clone_profile(self, name):
        directory = self.root / name
        shutil.copytree(self.initial, directory)
        manifest = dict(self.manifest)
        for field in ("caCertificatePath", "clientCertificatePath", "clientKeyPath"):
            manifest[field] = str(directory / Path(manifest[field]).name)
        (directory / "client-security.json").write_text(json.dumps(manifest))
        return directory

    def test_issue_has_real_chain_client_eku_and_independent_3072_key(self):
        cert = Path(self.manifest["clientCertificatePath"])
        key = Path(self.manifest["clientKeyPath"])
        self.command("verify", "-CAfile", self.ca / "ca-cert.pem", "-purpose", "sslclient", cert)
        details = self.command("x509", "-in", cert, "-text", "-noout")
        self.assertIn("CA:FALSE", details)
        self.assertIn("TLS Web Client Authentication", details)
        self.assertNotIn("TLS Web Server Authentication", details)
        self.assertIn("3072 bit", details)
        self.assertIn("CN=MedSoft HL7 Import", self.command("x509", "-in", cert, "-subject", "-nameopt", "RFC2253", "-noout"))
        self.assertEqual(self.command("pkey", "-in", key, "-pubout"), self.command("x509", "-in", cert, "-pubkey", "-noout"))
        self.assertNotEqual(self.command("pkey", "-in", key, "-pubout"), self.command("x509", "-in", self.ca / "ca-cert.pem", "-pubkey", "-noout"))
        self.assertNotIn("ENCRYPTED", key.read_text())
        self.assertEqual(len(self.manifest["issuerCaSha256"]), 64)
        self.assertEqual({path.name for path in self.initial.iterdir()},
                         {"client-cert.pem", "client-key.pem", "ca-cert.pem", "client-security.json"})

    def test_repeat_preserves_every_byte_without_needing_issuer_private_key(self):
        before = {path.name: path.read_bytes() for path in self.initial.iterdir()}
        with patch.object(client.certificates, "key_pair", side_effect=AssertionError("must not issue")):
            again = client.prepare_client(self.ca, self.initial, self.openssl)
        self.assertEqual(again, self.manifest)
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.initial.iterdir()})

    def test_existing_profile_from_other_ca_is_rejected_without_changes(self):
        before = {path.name: path.read_bytes() for path in self.initial.iterdir()}
        with self.assertRaisesRegex(ValueError, "другому CA"):
            client.prepare_client(self.other_ca, self.initial, self.openssl)
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.initial.iterdir()})

    def test_existing_unrelated_directory_is_preserved(self):
        directory = self.root / "unrelated"
        directory.mkdir(mode=0o700)
        (directory / "keep.txt").write_text("existing data")
        with self.assertRaises(ValueError):
            client.prepare_client(self.ca, directory, self.openssl)
        self.assertEqual((directory / "keep.txt").read_text(), "existing data")

    def test_existing_manifest_cannot_redirect_private_key_outside_profile(self):
        directory = self.clone_profile("redirect")
        manifest_path = directory / "client-security.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["clientKeyPath"] = str(self.ca / "ca-key.pem")
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "другой клиентский профиль"):
            client.prepare_client(self.ca, directory, self.openssl)

    def test_symlink_in_output_or_issuer_path_is_rejected(self):
        output_alias = self.root / "client-alias"
        output_alias.symlink_to(self.initial, target_is_directory=True)
        issuer_alias = self.root / "issuer-alias"
        issuer_alias.symlink_to(self.ca, target_is_directory=True)
        for issuer, output in [(self.ca, output_alias), (issuer_alias, self.root / "bad-output")]:
            with self.subTest(issuer=issuer, output=output), self.assertRaisesRegex(ValueError, "ссылки"):
                client.prepare_client(issuer, output, self.openssl)
        directory = self.clone_profile("key-symlink")
        key = directory / "client-key.pem"
        key.unlink()
        key.symlink_to(self.initial / "client-key.pem")
        with self.assertRaisesRegex(ValueError, "ссылки"):
            client.prepare_client(self.ca, directory, self.openssl)

    @unittest.skipIf(os.name == "nt", "POSIX file modes")
    def test_private_permissions_are_enforced_and_ca_key_never_copied(self):
        self.assertEqual(self.initial.stat().st_mode & 0o777, 0o700)
        for path in self.initial.iterdir():
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
        directory = self.clone_profile("readable-key")
        (directory / "client-key.pem").chmod(0o644)
        with self.assertRaisesRegex(ValueError, "0600"):
            client.prepare_client(self.ca, directory, self.openssl)
        self.assertFalse((self.initial / "ca-key.pem").exists())
        self.assertFalse((self.initial / "ca-key-password.txt").exists())

    def test_mismatched_leaf_private_key_is_rejected(self):
        directory = self.clone_profile("wrong-key")
        (directory / "client-key.pem").write_text(self.command("genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"))
        with self.assertRaisesRegex(ValueError, "не образуют пару"):
            client.prepare_client(self.ca, directory, self.openssl)

    def test_server_only_eku_is_rejected_even_when_signed_by_the_right_ca(self):
        directory = self.clone_profile("server-only")
        request = directory / "replacement.csr"
        self.command("req", "-new", "-key", directory / "client-key.pem", "-subj", "/CN=server-only", "-out", request)
        config = directory / "server.cnf"
        config.write_text("[extensions]\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\n")
        self.command("x509", "-req", "-days", "365", "-in", request, "-CA", self.ca / "ca-cert.pem",
                     "-CAkey", self.ca / "ca-key.pem", "-passin", "file:" + str(self.ca / "ca-key-password.txt"),
                     "-set_serial", "0x123456", "-extfile", config, "-extensions", "extensions",
                     "-out", directory / "client-cert.pem")
        with self.assertRaises(ValueError):
            client.prepare_client(self.ca, directory, self.openssl)

    def test_expired_certificate_is_rejected_without_renewal(self):
        directory = self.clone_profile("expired")
        request = directory / "expired.csr"
        self.command("req", "-new", "-key", directory / "client-key.pem", "-subj", "/CN=expired", "-out", request)
        config = directory / "expired.cnf"
        config.write_text("[extensions]\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=clientAuth\n")
        self.command("x509", "-req", "-days", "0", "-in", request, "-CA", self.ca / "ca-cert.pem",
                     "-CAkey", self.ca / "ca-key.pem", "-passin", "file:" + str(self.ca / "ca-key-password.txt"),
                     "-set_serial", "0x654321", "-extfile", config, "-extensions", "extensions",
                     "-out", directory / "client-cert.pem")
        before = {path.name: path.read_bytes() for path in directory.iterdir()}
        with self.assertRaises(ValueError):
            client.prepare_client(self.ca, directory, self.openssl)
        self.assertEqual(before, {path.name: path.read_bytes() for path in directory.iterdir()})

    def test_output_inside_repository_is_rejected_before_issuing(self):
        path = client.REPOSITORY / "artifacts/client-certificate-test"
        with self.assertRaisesRegex(ValueError, "вне репозитория"):
            client.prepare_client(self.ca, path, self.openssl)
        self.assertFalse(path.exists())

    def test_failed_issuance_cleans_only_the_new_directory(self):
        directory = self.root / "failed-issuance"
        before = {path.name: path.read_bytes() for path in self.ca.iterdir()}
        command = client._command
        def fail_request(openssl, *args):
            if args[0] == "req":
                raise ValueError("synthetic issuance failure")
            return command(openssl, *args)
        with patch.object(client, "_command", side_effect=fail_request):
            with self.assertRaisesRegex(ValueError, "synthetic"):
                client.prepare_client(self.ca, directory, self.openssl)
        self.assertFalse(directory.exists())
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.ca.iterdir()})

    def test_cli_prints_only_paths_and_hides_issuer_password(self):
        result = subprocess.run([sys.executable, str(SCRIPT), "--ca-directory", str(self.ca),
                                 "--output-directory", str(self.initial), "--openssl", self.openssl],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(self.manifest["clientCertificatePath"], result.stdout)
        self.assertNotIn("PRIVATE KEY", result.stdout + result.stderr)
        self.assertNotIn("Synthetic-CA.Password", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
