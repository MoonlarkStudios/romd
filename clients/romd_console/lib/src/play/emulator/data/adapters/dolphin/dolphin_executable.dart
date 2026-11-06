import 'package:path/path.dart' as p;

String dolphinProcessExecutable(String executablePath) =>
    executablePath.endsWith('.app')
    ? p.join(executablePath, 'Contents', 'MacOS', 'Dolphin')
    : executablePath;
