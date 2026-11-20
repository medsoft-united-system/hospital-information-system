#!/usr/bin/env python3
"""Run the explicit Linux TLS 1.3 transport test profile against isolated test data."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid


REPOSITORY = Path(__file__).resolve().parent.parent
PROJECT = "tests/HospitalInformationSystem.IntegrationTests/HospitalInformationSystem.IntegrationTests.csproj"


def copy_sources(destination):
    result = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        cwd=REPOSITORY, capture_output=True, check=True)
    count = 0
    for entry in result.stdout.split(bytes([0])):
        if not entry:
            continue
        relative = Path(os.fsdecode(entry))
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError("Invalid repository source path.")
        source = REPOSITORY / relative
        if not source.exists():
            continue
        if source.is_symlink() or not source.is_file():
            raise ValueError("The transport test snapshot requires regular source files.")
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        count += 1
    return count


def run(arguments):
    docker = shutil.which(arguments.docker)
    if docker is None:
        raise ValueError("Docker is required for the Linux TLS 1.3 test profile.")
    probe = subprocess.run([docker, "info", "--format", "{{.ServerVersion}}"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if probe.returncode != 0:
        raise ValueError("Start Docker before running the transport test profile.")
    with tempfile.TemporaryDirectory(prefix="medsoft-lab4-transport-") as temporary:
        snapshot = Path(temporary)
        count = copy_sources(snapshot)
        container_name = "medsoft-lab4-transport-" + uuid.uuid4().hex
        command = [docker, "run", "--rm", "--init", "--name", container_name,
                   "--mount", "type=bind,source=" + str(snapshot) + ",target=/workspace",
                   "--mount", "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock",
                   "--workdir", "/workspace",
                   "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                   "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
                   "--env", "TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal",
                   "--env", "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock"]
        if sys.platform.startswith("linux"):
            command += ["--add-host", "host.docker.internal:host-gateway"]
            if os.getuid() != 0:
                command += ["--user", str(os.getuid()) + ":" + str(os.getgid()),
                            "--group-add", str(Path("/var/run/docker.sock").stat().st_gid),
                            "--env", "HOME=/tmp/medsoft-home",
                            "--env", "DOTNET_CLI_HOME=/tmp/medsoft-dotnet",
                            "--env", "NUGET_PACKAGES=/tmp/medsoft-nuget"]
        packages = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))).expanduser().resolve()
        restore_arguments = []
        if packages.is_dir():
            command += ["--mount", "type=bind,source=" + str(packages) + ",target=/nuget-cache,readonly"]
            restore_arguments = ["-p:RestoreFallbackFolders=/nuget-cache"]
        command += [arguments.image, "dotnet", "test", PROJECT,
                    "--filter", ("Category=MllpTls|FullyQualifiedName~Lab4UpgradeFromLab3Tests"
                                 if arguments.include_upgrade else "Category=MllpTls"), "-m:1",
                    "-p:EnableMllpTlsTests=true", "-p:UseSharedCompilation=false",
                    "-p:RestoreIgnoreFailedSources=true", *restore_arguments]
        print("Linux TLS 1.3 profile: copied", count, "repository source files; ignored files stay outside the container.", flush=True)
        try:
            return subprocess.run(command).returncode
        finally:
            subprocess.run([docker, "rm", "--force", container_name],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="mcr.microsoft.com/dotnet/sdk:10.0",
                        help="Linux .NET 10 SDK image used for the TLS 1.3 test profile.")
    parser.add_argument("--include-upgrade", action="store_true",
                        help="Also verify migration from the last Lab3 schema with existing credentials and clinical data.")
    parser.add_argument("--docker", default="docker", help="Path to Docker CLI.")
    arguments = parser.parse_args()
    try:
        return run(arguments)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print("Transport verification failed:", str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
