#!/usr/bin/env python3
"""Normalize the upstream packer's ZIP and verify the installable artifact."""
import hashlib
import json
import sys
import zipfile
from pathlib import Path
from typing import cast


def verify(directory: Path) -> None:
    name = "BTCPayServer.Plugins.Fireblocks"
    archive = directory / f"{name}.btcpay"
    metadata = directory / f"{name}.btcpay.json"
    info = cast(dict[str, object], json.loads(metadata.read_text()))
    assert info["Identifier"] == name, info
    assert info["Version"] == directory.name, info
    # Stable ordering, timestamps and permissions: publishing twice must not
    # change the package merely because file mtimes changed.
    with zipfile.ZipFile(archive) as source:
        entries = {entry.filename: source.read(entry) for entry in source.infolist()}
    assert f"{name}.dll" in entries, "Missing plugin assembly at archive root"
    assert f"{name}.deps.json" in entries, "Missing dependency manifest"
    assert "BTCPayServer.dll" not in entries, "Host must not be bundled"
    for filename in entries:
        path = Path(filename)
        assert not path.is_absolute() and ".." not in path.parts, filename
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        for filename, data in sorted(entries.items()):
            entry = zipfile.ZipInfo(filename, date_time=(1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            target.writestr(entry, data, compresslevel=9)
    # Hashes must describe the normalized archive, not the packer's original ZIP.
    files = [metadata, archive]
    _ = (directory / "SHA256SUMS").write_text("".join(
        f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}\n" for file in files
    ))
    with zipfile.ZipFile(archive) as package:
        assert package.testzip() is None, "Corrupt archive"
        # All publish content, including future assets/dependencies, is preserved.
        published = Path("artifacts/publish")
        expected = {str(p.relative_to(published)): p.read_bytes()
                    for p in published.rglob("*") if p.is_file()}
        assert entries == expected, "Archive does not match publish output"
    print(f"Verified {archive} ({len(entries)} files), metadata and SHA256SUMS")


if __name__ == "__main__":
    verify(Path(sys.argv[1]))
