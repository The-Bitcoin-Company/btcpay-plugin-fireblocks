"""Regression checks for package verification; no third-party dependencies."""
import hashlib
import importlib.util
import json
import os
import tempfile
import zipfile
from collections.abc import Callable
from pathlib import Path
from typing import cast

spec = importlib.util.spec_from_file_location("verify_package", Path(__file__).with_name("verify_package.py"))
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
verify = cast(Callable[[Path], None], module.verify)


def pack(archive: Path, files: dict[str, bytes]) -> None:
    with zipfile.ZipFile(archive, "w") as target:
        for filename, data in files.items():
            target.writestr(filename, data)


def rejects(check: Callable[[], None]) -> None:
    try:
        check()
    except AssertionError:
        return
    raise AssertionError("Verifier accepted an invalid package")


def main() -> None:
    previous = Path.cwd()
    with tempfile.TemporaryDirectory() as temporary:
        try:
            os.chdir(temporary)
            name = "BTCPayServer.Plugins.Fireblocks"
            directory = Path("package/0.1.0")
            directory.mkdir(parents=True)
            archive = directory / f"{name}.btcpay"
            files = {f"{name}.dll": b"assembly", f"{name}.deps.json": b"{}",
                     "wwwroot/nested/asset.txt": b"required content"}
            for filename, data in files.items():
                target = Path("artifacts/publish") / filename
                target.parent.mkdir(parents=True, exist_ok=True)
                _ = target.write_bytes(data)
            metadata = directory / f"{name}.btcpay.json"
            _ = metadata.write_text(json.dumps({"Identifier": name, "Version": "0.1.0"}))
            pack(archive, files)
            verify(directory)
            first = archive.read_bytes()
            # Entry order and original timestamps cannot affect normalized bytes.
            pack(archive, dict(reversed(list(files.items()))))
            verify(directory)
            assert first == archive.read_bytes(), "Archive is not repeatable"
            for line in (directory / "SHA256SUMS").read_text().splitlines():
                digest, filename = line.split("  ")
                assert digest == hashlib.sha256((directory / filename).read_bytes()).hexdigest()
            invalid_packages = [
                {k: v for k, v in files.items() if not k.endswith(".dll")},
                {**files, "BTCPayServer.dll": b"host"},
                {**files, "../escape": b"bad"},
                {k: v for k, v in files.items() if not k.startswith("wwwroot/")},
            ]
            for invalid in invalid_packages:
                pack(archive, invalid)
                rejects(lambda: verify(directory))
            pack(archive, files)
            _ = metadata.write_text(json.dumps({"Identifier": name, "Version": "0.2.0"}))
            rejects(lambda: verify(directory))
        finally:
            os.chdir(previous)
    print("Package regression checks passed (repeatability, checksums, content and rejection cases)")


if __name__ == "__main__":
    main()
