'''Local SolidBrew pilot client. CAD source folders are read-only.'''
import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import Path

if __package__:
    from .cad_archive import ARCHIVE_LIMIT, capture, check_names, restore
else:
    from cad_archive import ARCHIVE_LIMIT, capture, check_names, restore

ENV_FILE = Path(__file__).resolve().parents[1] / '.env'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Api:
    def __init__(self, url):
        parts = urllib.parse.urlsplit(url)
        if (parts.scheme not in ('http', 'https') or not parts.hostname or parts.username or parts.password
            or parts.path not in ('', '/') or parts.query or parts.fragment
            or (parts.scheme == 'http' and parts.hostname not in ('127.0.0.1', 'localhost', '::1'))):
            raise ValueError('Pass a loopback HTTP URL or an HTTPS URL with no path.')
        self.url = url.rstrip('/')
        self.token = os.getenv('PDM_API_TOKEN')
        if not self.token and ENV_FILE.is_file():
            for line in ENV_FILE.read_text(encoding='utf-8-sig').splitlines():
                if line.startswith('PDM_API_TOKEN='):
                    self.token = line.split('=', 1)[1].strip()
                    break
        if not self.token or len(self.token) < 32:
            raise ValueError('Set PDM_API_TOKEN or initialize .env.')
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, endpoint, data=None, content=None, length=None):
        headers = {'Authorization': f'Bearer {self.token}'}
        if content is not None:
            headers.update({'Content-Type': content, 'Content-Length': str(length)})
        request = urllib.request.Request(self.url + endpoint, data=data, headers=headers)
        return self.opener.open(request, timeout=120)

    def json(self, endpoint, data=None):
        body = json.dumps(data, ensure_ascii=False).encode() if data is not None else None
        with self.request(endpoint, body, 'application/json' if body else None, len(body) if body else None) as response:
            return json.load(response)


def entry_args(parser):
    parser.add_argument('--assembly', required=True, help='exact relative SLDASM path in the snapshot')
    parser.add_argument('--drawing', required=True, help='exact relative SLDDRW path in the snapshot')


def run():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:8088')
    sub = parser.add_subparsers(dest='action', required=True)
    pack = sub.add_parser('capture', help='create a local ZIP without modifying CAD sources')
    pack.add_argument('--source', type=Path, required=True)
    pack.add_argument('--output', type=Path, required=True)
    entry_args(pack)
    sub.add_parser('items')
    item = sub.add_parser('new-item')
    item.add_argument('designation')
    item.add_argument('name')
    item.add_argument('--kind', choices=('part', 'assembly', 'complex', 'kit', 'undetermined'), required=True)
    for name in ('upload', 'history', 'restore'):
        command = sub.add_parser(name)
        command.add_argument('item', type=uuid.UUID)
        if name == 'upload':
            command.add_argument('archive', type=Path)
            entry_args(command)
        if name == 'restore':
            command.add_argument('version', type=int)
            command.add_argument('workspace', type=Path)
    args = parser.parse_args()
    if args.action == 'capture':
        return capture(args)
    api = Api(args.url)
    if args.action == 'items':
        return api.json('/api/items')
    if args.action == 'new-item':
        return api.json('/api/items', {'designation': args.designation, 'name': args.name, 'kind': args.kind})
    route = f'/api/items/{args.item}/snapshots'
    if args.action == 'history':
        return api.json(route)
    if args.action == 'restore':
        return restore(api, f'{route}/{args.version}', args.workspace)
    query = urllib.parse.urlencode({'assembly': args.assembly, 'drawing': args.drawing})
    with args.archive.open('rb') as archive:
        size = os.fstat(archive.fileno()).st_size
        if not 0 < size <= ARCHIVE_LIMIT:
            raise ValueError('Archive is empty or exceeds the pilot upload limit.')
        with api.request(f'{route}?{query}', archive, 'application/zip', size) as response:
            snapshot = json.load(response)
    return {**snapshot, 'files': len(snapshot['files'])}


if __name__ == '__main__':
    try:
        print(json.dumps(run(), ensure_ascii=True, indent=2))
    except urllib.error.HTTPError as error:
        detail = error.read(4096).decode(errors='replace')
        sys.exit(f'HTTP {error.code}: {detail}')
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        sys.exit(str(error))
