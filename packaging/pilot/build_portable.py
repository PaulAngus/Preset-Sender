"""Build portable pilot ZIPs on a development machine; no tools are installed on targets."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import uuid
import zipfile
from datetime import datetime, timezone

REPO = Path(__file__).resolve().parents[2]
TEMPLATES = Path(__file__).resolve().parent


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def git(*args):
    return subprocess.check_output(['git', '-C', str(REPO), *args])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--work', required=True)
    parser.add_argument('--rid', action='append', choices=['win-x64', 'osx-arm64'])
    parser.add_argument('--connection', help='Public dashboard-connection.json from the development PC (no pairing secrets)')
    args = parser.parse_args()
    connection = None
    if args.connection:
        connection = json.loads(Path(args.connection).read_text(encoding='utf-8-sig'))
        if set(connection) != {'server', 'certificateSha256'} or not connection['server'].startswith('https://') or len(connection['certificateSha256']) != 64:
            raise RuntimeError('Expected public dashboard connection information only')
    output, work = Path(args.output).resolve(), Path(args.work).resolve()
    output.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)
    rids = args.rid or ['win-x64', 'osx-arm64']
    if 'win-x64' in rids:
        subprocess.run(['pwsh', '-NoProfile', '-File', str(REPO / 'Initialize-MidiSdk.ps1')], cwd=REPO, check=True)
    source_paths = sorted(set(p.decode('utf-8') for p in git('ls-files', '-z', '--cached', '--others', '--exclude-standard').split(b'\0') if p))
    source = dict(head=git('rev-parse', 'HEAD').decode().strip(), workingTree=git('status', '--porcelain').decode().splitlines(), files=[dict(path=p, sha256=sha(REPO / p)) for p in source_paths if (REPO / p).is_file()])
    bundle_id = datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6]
    for rid in rids:
        label = 'Windows' if rid.startswith('win-') else 'Mac'
        root = work / (bundle_id + '-' + label) / ('PresetSender-Pilot-' + label)
        root.mkdir(parents=True)
        common = root / 'bin'
        common.mkdir()
        projects = [('tools/PresetSender.Lab/PresetSender.Lab.csproj', 'lab'), ('tools/PresetSender.Pilot/PresetSender.Pilot.csproj', 'probe'), ('tools/PresetSender.PortableTests/PresetSender.PortableTests.csproj', 'tests'), ('PresetSender.csproj', 'app')]
        for project, name in projects:
            publish = work / (bundle_id + '-publish') / rid / name
            artifacts = work / (bundle_id + '-build') / rid / name
            command = ['dotnet', 'publish', project, '-c', 'Release', '-r', rid, '--self-contained', 'true', '--artifacts-path', str(artifacts), '-o', str(publish), '-p:PublishSingleFile=false', '-p:EnableMacOSCodeSign=false', '-p:SenderPlatform=' + ('Windows' if rid == 'win-x64' else 'macOS')]
            if name == 'app' and rid == 'win-x64':
                command += ['-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeAllContentForSelfExtract=true', '-p:EnableCompressionInSingleFile=true']
            result = subprocess.run(command, cwd=REPO, capture_output=True, text=True)
            (work / f'{bundle_id}-{rid}-{name}.log').write_text(result.stdout + result.stderr, encoding='utf-8')
            if result.returncode:
                raise RuntimeError(f'Publish failed for {rid}/{name}: {result.stdout[-6000:]} {result.stderr[-1000:]}')
            print(f'Published {rid}/{name}', flush=True)
            destination = root / 'app' if name == 'app' else common / name
            for file in publish.rglob('*'):
                if not file.is_file():
                    continue
                target = destination / file.relative_to(publish)
                if target.exists():
                    if sha(target) != sha(file):
                        raise RuntimeError(f'Conflicting shared publish file: {target}')
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, target)
        launcher_names = ['Setup-Windows.cmd', 'Run-Windows.cmd', 'Pair-with-Dashboard-Windows.cmd', 'Start-Agent-Windows.cmd'] if rid == 'win-x64' else ['Setup-Mac.command', 'Run-Mac.command', 'Prepare-on-Mac-DEV.command', 'Pair-with-Dashboard-Mac.command', 'Start-Agent-Mac.command']
        for name in launcher_names + ['START-HERE.html']:
            shutil.copy2(TEMPLATES / name, root / name)
        if connection is not None:
            (root / 'dashboard-connection.json').write_text(json.dumps(connection, indent=2), encoding='utf-8')
        for name in ['LICENSE', 'NOTICE']:
            if (REPO / name).exists():
                shutil.copy2(REPO / name, root / name)
        (root / 'source-identity.json').write_text(json.dumps(source, indent=2), encoding='utf-8')
        forbidden = {'dotnet', 'dotnet.exe', 'pwsh', 'pwsh.exe', 'powershell.exe'}
        if any(p.name.lower() in forbidden for p in root.rglob('*')):
            raise RuntimeError('Forbidden SDK/PowerShell host found in target bundle')
        # Connection data is public, replaceable deployment configuration. Pin it by transferring it through a trusted channel.
        files = [dict(path=p.relative_to(root).as_posix(), sha256=sha(p)) for p in sorted(root.rglob('*')) if p.is_file() and p.name != 'dashboard-connection.json']
        manifest = dict(schemaVersion=1, bundleId=bundle_id, runtimeId=rid, signing='Prepare once on the Mac development VM before transfer.' if rid == 'osx-arm64' else 'Private Windows test bundle', files=files)
        (root / 'bundle-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        archive = output / f'PresetSender-Pilot-{label}.zip'
        with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as package:
            for file in sorted(root.rglob('*')):
                if file.is_file():
                    info = zipfile.ZipInfo.from_file(file, arcname=file.relative_to(root.parent).as_posix())
                    info.create_system = 3
                    executable = rid == 'osx-arm64' and (file.suffix in {'.command', '.dylib'} or file.name in {'PresetSender', 'PresetSender.Lab', 'PresetSender.Pilot', 'PresetSender.Tests', 'createdump'})
                    info.external_attr = (0o100755 if executable else 0o100644) << 16
                    package.writestr(info, file.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
        (output / (archive.name + '.sha256')).write_text(sha(archive) + '  ' + archive.name + '\n', encoding='ascii')
        print(f'Created {archive} ({archive.stat().st_size / 1024 / 1024:.1f} MiB); staging folder {root}', flush=True)


if __name__ == '__main__':
    main()
