import 'package:path/path.dart' as p;

/// Converts a resolved runtime artifact into the executable path the process
/// runner can start. macOS `.app` bundles are directories, so launch their
/// inner binary directly; all other artifacts are already executable files.
String runtimeProcessExecutable(String executablePath) =>
    executablePath.endsWith('.app')
    ? p.join(
        executablePath,
        'Contents',
        'MacOS',
        p.basenameWithoutExtension(executablePath),
      )
    : executablePath;
