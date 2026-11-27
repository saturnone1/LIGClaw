"""Rebuild pinned Cline SDKs without Dify and with the embedded Zod size fix.

Usage: python scripts/vendor-cline-without-dify.py <llms.tgz> --agents <agents.tgz> --shared <shared.tgz>
The input must match the published npm integrity. Normal npm ci needs no Python.
"""

import argparse
import base64
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import subprocess

UPSTREAM_INTEGRITY = (
    "sha512-gkahUaJ1rRQsvJ2uQ8a24lNhMBQ381GQVI2uz/Q4oxvHOUMOS11v6q7tK8ovAIGD+6uXyYQiOxQ4W4hN00zKkw=="
)
VERSION = "0.0.65-ligclaw.2"
DIFY_REGISTRATION = (
    '{id:"dify",name:"Dify",description:"Dify workflow/application provider via AI SDK",'
    'family:"dify",defaultModelId:"default",apiKeyEnv:["DIFY_API_KEY"],modelsFactory:()=>({})}'
)
FACTORIES = {
    "package/dist/index.js": (
        'import{createDifyProvider as X1}from"dify-ai-provider";',
        'async function nU(e){let t=await Y(e),n=X1({baseURL:e.baseUrl,headers:e.headers,fetch:e.fetch,...Yi(e)});return{model:(r)=>n(r,{apiKey:t})}}',
        'async function nU(){throw new Error("Dify is not included in this LIGClaw build.")}',
    ),
    "package/dist/providers.js": (
        'import{createDifyProvider as j1}from"dify-ai-provider";',
        'async function C1(e){let t=await X(e),n=j1({baseURL:e.baseUrl,headers:e.headers,fetch:e.fetch,...Mi(e)});return{model:(r)=>n(r,{apiKey:t})}}',
        'async function C1(){throw new Error("Dify is not included in this LIGClaw build.")}',
    ),
}


def replace_once(text, before, after, filename):
    if text.count(before) != 1:
        raise ValueError(f"Upstream patch precondition changed: {filename}")
    return text.replace(before, after, 1)


def build(upstream, package_name, expected_integrity, bundled_count):
    archive = upstream.read_bytes()
    integrity = "sha512-" + base64.b64encode(hashlib.sha512(archive).digest()).decode()
    if integrity != expected_integrity:
        raise ValueError("The upstream archive does not match the pinned npm integrity")
    files = {}
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as source:
        for entry in source:
            if entry.isdir():
                continue
            if not entry.isfile() or not entry.name.startswith("package/") or ".." in Path(entry.name).parts:
                raise ValueError(f"Unexpected archive entry: {entry.name}")
            files[entry.name] = source.extractfile(entry).read()
    package = json.loads(files["package/package.json"])
    if package["name"] != package_name or package["version"] != "0.0.65":
        raise ValueError("Unexpected upstream package")
    model_sdk = package_name == "@cline/llms"
    if model_sdk:
        del package["dependencies"]["dify-ai-provider"]
    package["version"] = VERSION
    package["license"] = "Apache-2.0"
    package["ligclawPatch"] = "Remove unused Dify from model SDK; backport upstream Zod collection size gate"
    files["package/package.json"] = (json.dumps(package, indent=2) + "\n").encode()
    changed = set()
    registrations = 0
    for name in sorted(files):
        if not name.endswith(".js") or not model_sdk:
            continue
        original = text = files[name].decode()
        if DIFY_REGISTRATION in text:
            text = replace_once(text, DIFY_REGISTRATION + ",", "", name)
            registrations += 1
        if name in FACTORIES:
            import_text, factory, rejection = FACTORIES[name]
            text = replace_once(text, import_text, "", name)
            text = replace_once(text, factory, rejection, name)
        if 'from"dify-ai-provider"' in text or 'import("dify-ai-provider")' in text:
            raise ValueError(f"A Dify import remains in {name}")
        if text != original:
            files[name] = text.encode()
            changed.add(name.removeprefix("package/"))
    if registrations != (4 if model_sdk else 0):
        raise ValueError("The reviewed registry layout changed")
    root = Path(__file__).resolve().parents[1]
    vendor = root / "sidecar/vendor"
    bundled = {name: data.decode() for name, data in files.items()
               if name.endswith(".js") and re.search(r"major:4,minor:4,patch:3", data.decode())}
    if len(bundled) != bundled_count:
        raise ValueError("The reviewed bundled Zod layout changed")
    patched = json.loads(subprocess.check_output(
        ["node", str(root / "scripts/patch-bundled-zod.mjs")],
        input=json.dumps(bundled), text=True, encoding="utf-8"))
    for name, text in patched.items():
        files[name] = text.encode()
        changed.add(name.removeprefix("package/"))
    files["package/LICENSE"] = (vendor / "LICENSE.cline").read_text(encoding="utf-8").encode()
    files["package/LICENSE.zod"] = (vendor / "LICENSE.zod").read_text(encoding="utf-8").encode()
    files["package/LIGCLAW-PATCH.txt"] = (
        f"{package_name} {VERSION}; derived from npm {package_name}@0.0.65.\n"
        "Cline upstream: https://github.com/cline/cline (Apache-2.0).\n"
        "Modified by LIGClaw on 2026-11-27: remove unused Dify from the model SDK;\n"
        "backport collection size bounds before element parsing in bundled Zod 4.4.3.\n"
        "Zod upstream fix: a0ebf970f2d752e5f58527cd2942ff48be4f46bd (MIT).\n"
        f"Original npm integrity: {expected_integrity}\n"
        "Recipe: scripts/vendor-cline-without-dify.py in the LIGClaw repository.\n"
    ).encode()
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w", format=tarfile.PAX_FORMAT) as target:
        for name, content in sorted(files.items()):
            entry = tarfile.TarInfo(name)
            entry.size, entry.mode, entry.mtime = len(content), 0o644, 0
            target.addfile(entry, io.BytesIO(content))
    short_name = package_name.split("/")[-1]
    output = vendor / f"cline-{short_name}-{VERSION}.tgz"
    with output.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as packed:
            packed.write(buffer.getvalue())
    manifest = {
        "upstream": f"{package_name}@0.0.65",
        "upstreamIntegrity": expected_integrity,
        "version": VERSION,
        "archive": output.name,
        "sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
        "modifiedRuntimeFiles": sorted(changed),
        "removedDependency": "dify-ai-provider" if model_sdk else None,
        "bundledZodBackport": "a0ebf970f2d752e5f58527cd2942ff48be4f46bd",
    }
    (vendor / f"cline-{short_name}-provenance.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("upstream", type=Path)
    parser.add_argument("--agents", type=Path, required=True)
    parser.add_argument("--shared", type=Path, required=True)
    args = parser.parse_args()
    build(args.upstream, "@cline/llms", UPSTREAM_INTEGRITY, 3)
    build(args.agents, "@cline/agents", "sha512-g4VfaFlXvFl9tAN0tVF9Zv517XT07fGN3c3Uxgpa0KljkvqPNBMUmx3m8oTPyv6rIQJUv3TLh2DVAPAgGaFgbQ==", 1)
    build(args.shared, "@cline/shared", "sha512-x5xD6cnNno4dWMqLCEmZ5qfiJnaI5dziyBYQLaX5+HQzCbTqFHXclH0Css10QkrrIfTiyIUbajpMBoGCUAOIyA==", 3)
