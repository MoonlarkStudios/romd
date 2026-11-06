"""Exercise fuzz-runner architecture and mount ownership without a container engine."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).with_name('fuzz.sh')


class FuzzRunnerTests(unittest.TestCase):
    def invoke(self, host='Linux-x86_64', engine='x86_64', rootless=False, arguments=('canary',), image_arch=None):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            script = root / 'scripts/native/fuzz.sh'
            script.parent.mkdir(parents=True)
            shutil.copyfile(SOURCE, script)
            pin = root / 'native/libchdr/libchdr.json'
            pin.parent.mkdir(parents=True)
            pin.write_text('{"commit":"pinned"}')
            marker = pin.parent / 'fuzz/marker.txt'
            marker.parent.mkdir()
            marker.write_text('original')
            fixtures = root / 'artifacts/fixtures/libchdr-final'
            fixtures.mkdir(parents=True)
            (fixtures / 'synthetic.chd').write_bytes(b'synthetic')
            policy = root / 'tools/Moonlark.Native.Engineering/Native/NativePolicy.cs'
            policy.parent.mkdir(parents=True)
            policy.write_text('internal const int BuildRecipeVersion = 2;\n')
            binaries = root / 'bin'
            binaries.mkdir()
            programs = {
                'uname': '#!/bin/sh\ncase "$1" in -s) echo "${FAKE_HOST%-*}" ;; -m) echo "${FAKE_HOST#*-}" ;; esac\n',
                'git': '#!/bin/sh\ncase "$*" in *rev-parse*) echo pinned ;; esac\n',
                'dotnet': '#!/usr/bin/env python3\nimport pathlib,sys\np=pathlib.Path(sys.argv[-1]);p.mkdir(parents=True,exist_ok=True);(p/"bad.chd").write_bytes(b"bad")\n',
                'docker': '#!/usr/bin/env python3\nimport json,os,sys\nwith open(os.environ["CALLS"],"a") as f: f.write(json.dumps(sys.argv[1:])+"\\n")\nif sys.argv[1]=="info": print(os.environ["FAKE_INFO"])\nelif sys.argv[1:3]==["image","inspect"]: print(os.environ["FAKE_IMAGE_ARCH"])\nif sys.argv[1]=="run":\n import pathlib\n source=next(pathlib.Path(v.rsplit(":",2)[0]) for v in sys.argv if v.endswith(":/src:ro"))\n assert (source/"fuzz/marker.txt").read_text()=="original", "source changed during gate"\n pathlib.Path(os.environ["SOURCE_MARKER"]).write_text("changed")\n output=next(pathlib.Path(v.rsplit(":",1)[0]) for v in sys.argv if v.endswith(":/out"))\n (output/"parity").mkdir(parents=True,exist_ok=True)\n',
            }
            for name, program in programs.items():
                executable = binaries / name
                executable.write_text(program)
                executable.chmod(0o755)
            calls = root / 'calls.jsonl'
            environment = dict(os.environ, PATH=str(binaries) + os.pathsep + os.environ['PATH'],
                               FAKE_HOST=host, CALLS=str(calls), SOURCE_MARKER=str(marker),
                               FAKE_IMAGE_ARCH=image_arch or ('arm64' if engine in ('aarch64', 'arm64') else 'amd64'),
                               FAKE_INFO=json.dumps({'Architecture': engine,
                                                    'SecurityOptions': ['name=rootless'] if rootless else []}))
            result = subprocess.run(['bash', str(script), *arguments], env=environment,
                                    capture_output=True, text=True, timeout=15)
            commands = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            return result, commands

    def test_x64_campaign_uses_native_platform_and_separate_output(self):
        result, commands = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        run = next(command for command in commands if command[0] == 'run')
        self.assertEqual('linux/amd64', run[run.index('--platform') + 1])
        self.assertEqual(f'{os.getuid()}:{os.getgid()}', run[run.index('--user') + 1])
        self.assertTrue(any(value.endswith('/artifacts/fuzz/linux-x64:/out') for value in run))
        self.assertIn('romd-libchdr-fuzz:amd64', run)
        self.assertIn('CHDR_FUZZ_OUTPUT=artifacts/fuzz/linux-x64', run)

    def test_entire_gate_uses_one_frozen_source_even_if_checkout_changes(self):
        result, commands = self.invoke(arguments=('gate', '1'))
        self.assertEqual(0, result.returncode, result.stderr)
        runs = [command for command in commands if command[0] == 'run']
        self.assertEqual(3, len(runs))
        mounts = [next(value for value in command if value.endswith(':/src:ro')) for command in runs]
        self.assertEqual(1, len(set(mounts)))
        self.assertIn('/artifacts/fuzz/linux-x64/source.', mounts[0])

    def test_arm64_and_apple_silicon_use_arm64_output(self):
        for host in ('Linux-aarch64', 'Linux-arm64', 'Darwin-arm64'):
            with self.subTest(host=host):
                result, commands = self.invoke(host=host, engine='aarch64')
                self.assertEqual(0, result.returncode, result.stderr)
                run = next(command for command in commands if command[0] == 'run')
                self.assertEqual('linux/arm64', run[run.index('--platform') + 1])
                self.assertTrue(any(value.endswith('/artifacts/fuzz/linux-arm64:/out') for value in run))

    def test_rootless_docker_uses_invoking_host_owner_mapping(self):
        result, commands = self.invoke(rootless=True)
        self.assertEqual(0, result.returncode, result.stderr)
        run = next(command for command in commands if command[0] == 'run')
        self.assertEqual('0:0', run[run.index('--user') + 1])

    def test_different_engine_architecture_cannot_silently_use_emulation(self):
        result, commands = self.invoke(engine='aarch64')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Docker architecture differs', result.stderr)
        self.assertFalse(any(command[0] in ('build', 'run') for command in commands))

    def test_wrong_built_image_architecture_cannot_run(self):
        result, commands = self.invoke(image_arch='arm64')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('built image architecture differs', result.stderr)
        self.assertTrue(any(command[0] == 'build' for command in commands))
        self.assertFalse(any(command[0] == 'run' for command in commands))

    def test_unknown_engine_architecture_fails_before_build(self):
        result, commands = self.invoke(engine='riscv64')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Docker must report a supported native architecture', result.stderr)
        self.assertFalse(any(command[0] in ('build', 'run') for command in commands))

    def test_unsupported_host_fails_before_engine_mutation(self):
        result, commands = self.invoke(host='Linux-riscv64')
        self.assertEqual(2, result.returncode)
        self.assertEqual([], commands)

    def test_invalid_duration_cannot_start_campaign(self):
        result, commands = self.invoke(arguments=('gate', '0'))
        self.assertEqual(2, result.returncode)
        self.assertEqual([], commands)


if __name__ == '__main__':
    unittest.main()
