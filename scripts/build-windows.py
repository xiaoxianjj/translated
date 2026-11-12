#!/usr/bin/env python3
"""Build the Windows desktop executable and its portable ZIP with a .NET SDK."""
import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parents[1]
project = root / "Windows"
dotnet = os.environ.get("DOTNET") or "dotnet"
subprocess.run([dotnet, "build", str(project / "Translated.Windows.csproj"), "-c", "Release", "--nologo"], check=True)
built = project / "bin" / "Release" / "net48"
output = root / "dist" / "Translated-Windows"
output.mkdir(parents=True, exist_ok=True)
names = ["Translated.exe", "Translated.exe.config", "使用说明.txt", "Nunito-OFL.txt"]
for name in names[:2]:
    shutil.copy2(built / name, output / name)
guide = (project / names[2]).read_text(encoding="utf-8").replace("\n", "\r\n")
(output / names[2]).write_bytes(guide.encode("utf-8-sig"))
shutil.copy2(project / "Assets" / names[3], output / names[3])
archive = output.with_suffix(".zip")
with tempfile.TemporaryDirectory(dir=output.parent) as temp:
    pack = Path(temp) / archive.name
    with ZipFile(pack, "w", compression=ZIP_DEFLATED) as bundle:
        for name in names:
            bundle.write(output / name, name)
    with ZipFile(pack) as bundle:
        if bundle.testzip() or any(bundle.read(name) != (output / name).read_bytes() for name in names):
            raise RuntimeError("Failed to verify Windows ZIP")
    os.replace(pack, archive)
print(archive)
