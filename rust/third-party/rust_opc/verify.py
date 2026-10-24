#!/usr/bin/env python3
"""Verify the complete selected upstream input closure and reviewed patch delta."""

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    root = Path(__file__).resolve().parent
    originals = json.loads((root / "upstream-files.sha256.json").read_text())
    own = {".gitattributes", "README.icem.md", "verify.py", "upstream-files.sha256.json", "icem.patch"}
    actual = {str(p.relative_to(root)).replace("\\", "/") for p in root.rglob("*") if p.is_file()}
    if actual != set(originals) | own:
        raise SystemExit(f"unexpected/missing vendor files: {sorted(actual ^ (set(originals) | own))}")
    if any(p.is_symlink() for p in root.rglob("*")):
        raise SystemExit("vendor symlinks are not permitted")
    with tempfile.TemporaryDirectory(prefix="icem-opc-vendor-") as directory:
        temp = Path(directory)
        for name in originals:
            target = temp / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(root / name, target)
        # Applying the exact reviewed delta in reverse must reconstruct every
        # original byte. No upstream checkout, network or Cargo cache edits.
        subprocess.run(["git", "apply", "--reverse", "--check", str(root / "icem.patch")], cwd=temp, check=True)
        subprocess.run(["git", "apply", "--reverse", str(root / "icem.patch")], cwd=temp, check=True)
        reconstructed = {str(p.relative_to(temp)).replace("\\", "/") for p in temp.rglob("*") if p.is_file()}
        if reconstructed != set(originals):
            raise SystemExit("patch reconstructed undeclared or missing upstream inputs")
        for name, expected in originals.items():
            if hashlib.sha256((temp / name).read_bytes()).hexdigest() != expected:
                raise SystemExit(f"upstream SHA256 mismatch: {name}")
    print(f"OPC DA vendor: {len(originals)} exact upstream inputs + reviewed safe delta PASS")


if __name__ == "__main__":
    main()
