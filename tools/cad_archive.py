import hashlib
import os
import stat
import tempfile
import unicodedata
import zipfile
from pathlib import Path

CHUNK = 1024 * 1024
ARCHIVE_LIMIT = 256 * CHUNK
EXPANDED_LIMIT = 512 * CHUNK
FILE_LIMIT = 10_000
INVALID = set('<>:|?*') | {chr(34), chr(92)}
RESERVED = {'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$'} | {p + n for p in ('COM', 'LPT') for n in '123456789¹²³'}


def safe_path(path):
    parts = path.split('/')
    if not path or len(path) > 1000 or any(
        not p or len(p) > 255 or p.endswith(('.', ' '))
        or any(c in INVALID or ord(c) < 32 or ord(c) == 127 for c in p)
        or p.split('.', 1)[0].rstrip().upper() in RESERVED for p in parts
    ):
        raise ValueError(f'Unsafe relative path: {path!r}')
    return parts


def check_names(paths):
    files, directories = set(), {}
    for path in paths:
        parts = safe_path(path)
        key = unicodedata.normalize('NFC', path).casefold()
        if key in files or key in directories:
            raise ValueError(f'Conflicting file path: {path!r}')
        files.add(key)
        for i in range(1, len(parts)):
            parent = '/'.join(parts[:i])
            key = unicodedata.normalize('NFC', parent).casefold()
            if key in files or directories.setdefault(key, parent) != parent:
                raise ValueError(f'Conflicting directory path: {parent!r}')


def is_link(info):
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, 'st_file_attributes', 0) & 0x400)


def signature(info):
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)


def fail_walk(error):
    raise error


def scan(source):
    files = {}
    total = 0
    for directory, subdirs, names in os.walk(source, onerror=fail_walk, followlinks=False):
        for name in subdirs:
            path = Path(directory) / name
            if is_link(path.lstat()):
                raise ValueError(f'Reparse point not supported: {path}')
        for name in names:
            path = Path(directory) / name
            info = path.lstat()
            if is_link(info) or not stat.S_ISREG(info.st_mode):
                raise ValueError(f'Not a regular file: {path}')
            files[path.relative_to(source).as_posix()] = (path, signature(info))
            total += info.st_size
            if total > EXPANDED_LIMIT or len(files) > FILE_LIMIT:
                raise ValueError('Source folder exceeds the pilot size or file count limit.')
    check_names(files)
    return files


def capture(args):
    if is_link(args.source.lstat()):
        raise ValueError('Source must not be a symlink or junction.')
    source = args.source.resolve(strict=True)
    if not source.is_dir():
        raise ValueError('Source must be a directory.')
    output = args.output.resolve()
    if output.is_relative_to(source) or output.exists():
        raise ValueError('Archive must be a new file outside the source folder.')
    files = scan(source)
    for name, extension in ((args.assembly, '.sldasm'), (args.drawing, '.slddrw')):
        if not name.lower().endswith(extension) or name not in files or not files[name][1][2]:
            raise ValueError(f'Missing or empty entry file: {name}')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='pdm-pack-', dir=output.parent) as temporary:
        staging = Path(temporary) / 'snapshot.zip'
        with zipfile.ZipFile(staging, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=3, strict_timestamps=False) as archive:
            for relative, (path, expected) in sorted(files.items()):
                archive.write(path, relative)
                if signature(path.lstat()) != expected:
                    raise ValueError(f'Source changed while reading: {relative}')
        if scan(source) != files:
            raise ValueError('Source folder changed while reading; retry with a stable copy.')
        if staging.stat().st_size > ARCHIVE_LIMIT:
            raise ValueError('Archive exceeds the pilot upload limit.')
        with staging.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        with zipfile.ZipFile(staging) as archive:
            if bad := archive.testzip():
                raise ValueError(f'ZIP verification failed: {bad}')
        if output.exists():
            raise ValueError('Target archive already exists.')
        staging.rename(output)
    return {'archive': str(output), 'files': len(files), 'archiveBytes': output.stat().st_size, 'sha256': digest}


def copy_verified(source, destination, size, digest):
    actual = hashlib.sha256()
    with destination.open('xb') as target:
        while block := source.read(CHUNK):
            size -= len(block)
            if size < 0:
                raise ValueError('Payload exceeded its recorded size.')
            actual.update(block)
            target.write(block)
    if size or actual.hexdigest() != digest:
        raise ValueError('Payload failed size or SHA-256 verification.')


def restore(api, route, workspace):
    destination = workspace.resolve()
    if destination.exists():
        raise ValueError('Restore needs a new workspace path.')
    manifest = api.json(route)
    expected = {item['path']: item for item in manifest['files']}
    if len(expected) != len(manifest['files']) or len(expected) > FILE_LIMIT:
        raise ValueError('Invalid manifest file count.')
    check_names(expected)
    sizes = [item['bytes'] for item in expected.values()]
    if any(not 0 <= size <= EXPANDED_LIMIT for size in sizes) or sum(sizes) > EXPANDED_LIMIT:
        raise ValueError('Invalid manifest file sizes.')
    size = manifest['archiveBytes']
    if not 0 < size <= ARCHIVE_LIMIT:
        raise ValueError('Invalid manifest archive size.')
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='pdm-restore-', dir=destination.parent) as temporary:
        root = Path(temporary)
        archive_path = root / 'download.zip'
        with api.request(route + '/archive') as response:
            copy_verified(response, archive_path, size, manifest['sha256'])
        staging = root / 'workspace'
        staging.mkdir()
        with zipfile.ZipFile(archive_path) as archive:
            members = archive.infolist()
            if len(members) != len(expected) or {m.filename for m in members} != set(expected):
                raise ValueError('Archive does not match the manifest.')
            for member in members:
                info = expected[member.filename]
                unix_type = (member.external_attr >> 16) & 0xf000
                if unix_type not in (0, stat.S_IFREG) or member.external_attr & 0x410:
                    raise ValueError('Archive contains a special entry.')
                if info['bytes'] != member.file_size:
                    raise ValueError('ZIP entry size mismatch.')
                path = staging.joinpath(*safe_path(member.filename))
                path.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source:
                    copy_verified(source, path, info['bytes'], info['sha256'])
        if destination.exists():
            raise ValueError('Workspace already exists.')
        staging.rename(destination)
    return {'workspace': str(destination), 'version': manifest['version'], 'status': manifest['status'],
            'files': len(expected), 'assembly': manifest['assembly'], 'drawing': manifest['drawing']}
