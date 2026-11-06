import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/registry/default_runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';

// A test-local descriptor so resolution behavior is verified independently of
// whichever real runtimes happen to be defined.
const _descriptor = RuntimeDescriptor(
  runtimeId: 'demo',
  executableNames: <String>['demo', 'demo.exe'],
  overrideKey: 'ROMD_DEMO_PATH',
);

final class _FakeProbeEnv implements RuntimeProbeEnvironment {
  _FakeProbeEnv({
    this.operatingSystem = 'linux',
    Map<String, String>? environment,
    this.buildDefineValue,
    this.pathDirectories = const <String>[],
    Set<String>? existing,
  }) : environment = environment ?? <String, String>{},
       _existing = existing ?? <String>{};

  @override
  final String operatingSystem;
  @override
  final String architecture = 'arm64';
  @override
  final Map<String, String> environment;
  @override
  final String managedRuntimesRoot = '/managed';
  @override
  final List<String> pathDirectories;

  final String? buildDefineValue;
  final Set<String> _existing;

  @override
  String? buildDefine(String key) =>
      key == _descriptor.overrideKey ? buildDefineValue : null;

  @override
  bool fileExists(String path) => _existing.contains(path);
}

void main() {
  final managedPath = p.join('/managed', 'demo', 'demo');

  Future<RuntimeResolution> resolve(_FakeProbeEnv env) =>
      DefaultRuntimeManager(environment: env).resolve(_descriptor);

  test('managed install is resolved first', () async {
    final result = await resolve(
      _FakeProbeEnv(
        buildDefineValue: '/opt/custom/demo',
        environment: <String, String>{'ROMD_DEMO_PATH': '/env/demo'},
        existing: <String>{managedPath, '/opt/custom/demo', '/env/demo'},
      ),
    );
    expect(result.source, RuntimeResolutionSource.managed);
    expect(result.executablePath, managedPath);
  });

  test('managed Linux RetroArch AppImage is discoverable', () async {
    final appImage = p.join(
      '/managed',
      'retroarch',
      'RetroArch-Linux-x86_64.AppImage',
    );
    final result = await DefaultRuntimeManager(
      environment: _FakeProbeEnv(existing: <String>{appImage}),
    ).resolve(retroArchRuntimeDescriptor);

    expect(result.source, RuntimeResolutionSource.managed);
    expect(result.executablePath, appImage);
  });

  test('build-define override wins over env and PATH', () async {
    final result = await resolve(
      _FakeProbeEnv(
        buildDefineValue: '/opt/custom/demo',
        environment: <String, String>{'ROMD_DEMO_PATH': '/env/demo'},
        pathDirectories: <String>['/usr/bin'],
        existing: <String>{
          '/opt/custom/demo',
          '/env/demo',
          p.join('/usr/bin', 'demo'),
        },
      ),
    );
    expect(result.source, RuntimeResolutionSource.buildDefine);
    expect(result.executablePath, '/opt/custom/demo');
  });

  test('env override is used when the build-define file is missing', () async {
    final result = await resolve(
      _FakeProbeEnv(
        buildDefineValue: '/opt/missing/demo',
        environment: <String, String>{'ROMD_DEMO_PATH': '/env/demo'},
        existing: <String>{'/env/demo'},
      ),
    );
    expect(result.source, RuntimeResolutionSource.environment);
    expect(result.executablePath, '/env/demo');
    expect(result.diagnostics, isNotEmpty);
  });

  test('falls back to PATH', () async {
    final onPath = p.join('/usr/bin', 'demo');
    final result = await resolve(
      _FakeProbeEnv(
        pathDirectories: <String>['/usr/bin'],
        existing: <String>{onPath},
      ),
    );
    expect(result.source, RuntimeResolutionSource.path);
    expect(result.executablePath, onPath);
  });

  test('falls back to common install locations', () async {
    final result = await resolve(
      _FakeProbeEnv(existing: <String>{p.join('/usr/local/bin', 'demo')}),
    );
    expect(result.source, RuntimeResolutionSource.commonInstallLocation);
    expect(result.executablePath, p.join('/usr/local/bin', 'demo'));
  });

  test('uses Windows common locations and the .exe name', () async {
    final exe = p.join(r'C:\Program Files', 'demo.exe');
    final result = await resolve(
      _FakeProbeEnv(
        operatingSystem: 'windows',
        environment: <String, String>{'ProgramFiles': r'C:\Program Files'},
        existing: <String>{exe},
      ),
    );
    expect(result.source, RuntimeResolutionSource.commonInstallLocation);
    expect(result.executablePath, exe);
  });

  test('reports notFound with diagnostics when nothing resolves', () async {
    final result = await resolve(_FakeProbeEnv());
    expect(result.source, RuntimeResolutionSource.notFound);
    expect(result.executablePath, isNull);
    expect(result.isAvailable, isFalse);
    expect(result.diagnostics, isNotEmpty);
  });
}
