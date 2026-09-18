import 'package:romd_console/src/play/session/domain/window_controller.dart';
import 'package:window_manager/window_manager.dart';

/// [WindowController] backed by `window_manager`.
final class WindowManagerController implements WindowController {
  const WindowManagerController();

  @override
  Future<void> reclaimForeground() async {
    await windowManager.show();
    await windowManager.focus();
  }
}
