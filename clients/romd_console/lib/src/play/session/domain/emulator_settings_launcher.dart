import 'launch_service.dart';

/// Opens an emulator's own configuration UI outside a gameplay session.
abstract interface class EmulatorSettingsLauncher {
  Future<LaunchResult> openDolphin();
}
