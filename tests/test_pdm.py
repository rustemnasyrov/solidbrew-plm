import hashlib
import io
import tempfile
import unittest
import zipfile
from argparse import Namespace
from pathlib import Path

from tools import pdm


class PilotClientTests(unittest.TestCase):
    def test_capture_preserves_names_and_bytes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'source'
            source.mkdir()
            assembly, drawing = 'Сборка.SLDASM', 'Сборка.SLDDRW'
            (source / assembly).write_bytes(b'model')
            (source / drawing).write_bytes(b'drawing')
            path = root / 'v1.zip'
            result = pdm.capture(Namespace(source=source, output=path, assembly=assembly, drawing=drawing))
            self.assertEqual(2, result['files'])
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), result['sha256'])
            with zipfile.ZipFile(path) as archive:
                self.assertEqual({assembly, drawing}, set(archive.namelist()))
                self.assertEqual(b'model', archive.read(assembly))
            self.assertEqual(b'model', (source / assembly).read_bytes())

    def test_paths_must_not_collide_on_windows(self):
        for paths in (['Folder/a', 'folder/b'], ['x', 'x/y'], ['file.SLDPRT', 'File.sldprt']):
            with self.subTest(paths=paths), self.assertRaises(ValueError):
                pdm.check_names(paths)

    def test_restore_verifies_manifest_before_publishing_workspace(self):
        files = {'Модель.SLDASM': b'model', 'Модель.SLDDRW': b'drawing'}
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            for name, content in files.items():
                archive.writestr(name, content)
        payload = stream.getvalue()
        manifest = {'version': 1, 'status': 'working', 'assembly': 'Модель.SLDASM', 'drawing': 'Модель.SLDDRW',
                    'archiveBytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest(),
                    'files': [{'path': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                              for name, data in files.items()]}

        class FakeApi:
            def json(self, route):
                return manifest

            def request(self, route):
                return io.BytesIO(payload)

        with tempfile.TemporaryDirectory() as folder:
            valid = Path(folder) / 'valid'
            pdm.restore(FakeApi(), '/snapshot', valid)
            self.assertEqual(files, {path.name: path.read_bytes() for path in valid.iterdir()})
            invalid = Path(folder) / 'invalid'
            manifest['files'][0]['sha256'] = '0' * 64
            with self.assertRaisesRegex(ValueError, 'verification'):
                pdm.restore(FakeApi(), '/snapshot', invalid)
            self.assertFalse(invalid.exists())


if __name__ == '__main__':
    unittest.main()
