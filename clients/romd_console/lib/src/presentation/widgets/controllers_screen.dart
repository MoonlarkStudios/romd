import 'dart:async';

import 'package:flutter/material.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

import '../glyph_family_scope.dart';
import '../theme/console_theme_context.dart';
import 'console_settings.dart';
import 'controller_setup_screen.dart';

/// Settings → Controllers: device-global controller setup and display
/// preferences. Session-only player order lives in the Players surface.
final class ControllersScreen extends StatefulWidget {
  const ControllersScreen({
    required this.preferences,
    this.controllerInputProvider = const GamepadsControllerInputProvider(),
    this.hardwareMappings,
    super.key,
  });

  final ControllerPreferencesRepository preferences;
  final ControllerInputProvider controllerInputProvider;
  final ControllerHardwareMappingRepository? hardwareMappings;

  @override
  State<ControllersScreen> createState() => _ControllersScreenState();
}

final class _ControllersScreenState extends State<ControllersScreen> {
  static const Duration _pollInterval = Duration(seconds: 2);

  ControllerPreferences _preferences = ControllerPreferences.defaults;
  Timer? _pollTimer;
  List<RawControllerDescriptor> _rawControllers =
      const <RawControllerDescriptor>[];
  Map<String, ControllerHardwareMapping?> _customMappings =
      const <String, ControllerHardwareMapping?>{};
  String? _rawControllerError;
  final Map<String, FocusNode> _layoutFocusNodes = <String, FocusNode>{};

  @override
  void initState() {
    super.initState();
    _load();
    _refreshControllers();
    _pollTimer = Timer.periodic(_pollInterval, (_) => _refreshControllers());
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    for (final node in _layoutFocusNodes.values) {
      node.dispose();
    }
    super.dispose();
  }

  Future<void> _load() async {
    final preferences = await widget.preferences.load();
    if (mounted) {
      setState(() => _preferences = preferences);
    }
  }

  Future<void> _refreshControllers() async {
    try {
      var raw = const <RawControllerDescriptor>[];
      String? rawError;
      final Object provider = widget.controllerInputProvider;
      if (provider is RawControllerInputProvider) {
        try {
          raw = await provider.listRawControllers();
        } on Object {
          rawError = 'Raw controller input is unavailable right now.';
        }
      } else {
        rawError = 'Controller setup requires the SDL controller service.';
      }
      final custom = <String, ControllerHardwareMapping?>{};
      final repository = widget.hardwareMappings;
      if (repository != null) {
        for (final descriptor in raw) {
          final guid = descriptor.sdlGuid;
          if (guid == null || guid.isEmpty) continue;
          final key = _hardwareKey(descriptor.sdlPlatform, guid);
          custom.putIfAbsent(key, () => null);
        }
        for (final key in custom.keys.toList(growable: false)) {
          final descriptor = raw.firstWhere(
            (item) => _hardwareKey(item.sdlPlatform, item.sdlGuid!) == key,
          );
          try {
            custom[key] = await repository.find(
              sdlPlatform: descriptor.sdlPlatform,
              sdlGuid: descriptor.sdlGuid!,
            );
          } on Object {
            rawError = 'Saved controller setups could not be loaded.';
          }
        }
      }
      if (mounted) {
        setState(() {
          _rawControllers = raw;
          _customMappings = custom;
          _rawControllerError = rawError;
        });
      }
    } on Object {
      // No controller backend — preferences remain keyboard-navigable.
    }
  }

  Future<void> _selectTemplate(ControllerTemplate template) async {
    await widget.preferences.saveTemplate(template.id);
    if (mounted) {
      // Hint bars and help text across the app re-label immediately.
      GlyphFamilyScope.maybeNotifierOf(context)?.value = template.glyphFamily;
      setState(
        () => _preferences = ControllerPreferences(templateId: template.id),
      );
    }
  }

  Future<void> _openSetup(_ControllerModelView model) async {
    final repository = widget.hardwareMappings;
    if (model.connectedUnits != 1 || repository == null) {
      return;
    }
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ControllerSetupScreen(
          repository: repository,
          descriptor: model.descriptor,
          inputProvider: widget.controllerInputProvider,
          connectedUnits: model.connectedUnits,
        ),
      ),
    );
    await _refreshControllers();
  }

  List<_ControllerModelView> _controllerModels() {
    final models = <String, _ControllerModelView>{};
    for (final descriptor in _rawControllers) {
      final guid = descriptor.sdlGuid;
      final key = guid?.isNotEmpty == true
          ? _hardwareKey(descriptor.sdlPlatform, guid!)
          : 'pad:${descriptor.controllerId}';
      final existing = models[key];
      models[key] = _ControllerModelView(
        focusKey: key,
        descriptor: existing?.descriptor ?? descriptor,
        connectedUnits: (existing?.connectedUnits ?? 0) + 1,
        customMapping: _customMappings[key],
      );
    }
    return models.values.toList(growable: false);
  }

  static String _hardwareKey(String platform, String guid) =>
      '${platform.trim().toLowerCase()}|${guid.trim().toLowerCase()}';

  @override
  Widget build(BuildContext context) {
    final selectedTemplateId =
        _preferences.templateId ?? BuiltinControllerTemplates.generic.id;
    final controllerModels = _controllerModels();

    return ConsoleSettingsHierarchicalPage(
      key: const ValueKey<String>('controllers-settings-screen'),
      eyebrow: 'Settings',
      title: 'Controllers',
      description:
          'Test or configure connected controller hardware and choose the '
          'button prompts Ottercade shows.',
      onBack: () => Navigator.of(context).maybePop(),
      categories: <ConsoleSettingsCategory>[
        ConsoleSettingsCategory(
          id: 'connected-controllers',
          icon: Icons.gamepad_outlined,
          title: 'Connected controllers',
          subtitle: 'Test and map device hardware',
          child: ConsoleSettingsSection(
            title: 'Controller hardware',
            description:
                'Open a controller to test live input or map its physical '
                'controls to the Ottercade controller.',
            children: <Widget>[
              if (_rawControllerError case final error?)
                _ControllerLayoutUnavailable(message: error)
              else if (controllerModels.isEmpty)
                const _ControllerLayoutEmpty()
              else
                for (final model in controllerModels)
                  _ControllerLayoutRow(
                    model: model,
                    editorAvailable: widget.hardwareMappings != null,
                    focusNode: _layoutFocusNodes.putIfAbsent(
                      model.focusKey,
                      () => FocusNode(
                        debugLabel: 'settings-row-${model.focusKey}',
                      ),
                    ),
                    onPressed: () => _openSetup(model),
                  ),
            ],
          ),
        ),
        ConsoleSettingsCategory(
          id: 'button-prompts',
          icon: Icons.gamepad,
          title: 'Button prompts',
          subtitle: 'Choose the labels Ottercade shows',
          child: ConsoleSettingsSection(
            title: 'Prompt style',
            description:
                'Choose the labels Ottercade shows. This does not change what '
                'your buttons do.',
            children: <Widget>[
              for (final template in BuiltinControllerTemplates.all)
                ConsoleSettingsChoiceRow(
                  icon: Icons.gamepad_outlined,
                  title: template.displayName,
                  selected: template.id == selectedTemplateId,
                  onPressed: () => _selectTemplate(template),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

final class _ControllerModelView {
  const _ControllerModelView({
    required this.focusKey,
    required this.descriptor,
    required this.connectedUnits,
    required this.customMapping,
  });

  final String focusKey;
  final RawControllerDescriptor descriptor;
  final int connectedUnits;
  final ControllerHardwareMapping? customMapping;

  String get displayName => descriptor.displayName;
  bool get configurable => descriptor.sdlGuid?.isNotEmpty == true;

  String get status {
    if (!configurable) return 'Test only';
    if (customMapping != null) return 'Custom';
    if (descriptor.isMappedGamepad && descriptor.sdlMapping != null) {
      return 'Detected';
    }
    return 'Needs setup';
  }
}

final class _ControllerLayoutEmpty extends StatelessWidget {
  const _ControllerLayoutEmpty();

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Semantics(
      container: true,
      label: 'No connected controllers available for button layout editing',
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: colors.panelSurface,
          borderRadius: BorderRadius.circular(layout.controlRadius),
        ),
        child: Padding(
          padding: layout.panelPadding,
          child: Text(
            'Connect a controller to configure its button layout.',
            style: context.text.body.copyWith(color: colors.textBody),
          ),
        ),
      ),
    );
  }
}

final class _ControllerLayoutUnavailable extends StatelessWidget {
  const _ControllerLayoutUnavailable({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: colors.panelSurface,
        borderRadius: BorderRadius.circular(layout.controlRadius),
      ),
      child: Padding(
        padding: layout.panelPadding,
        child: Text(
          message,
          style: context.text.body.copyWith(color: colors.textBody),
        ),
      ),
    );
  }
}

final class _ControllerLayoutRow extends StatelessWidget {
  const _ControllerLayoutRow({
    required this.model,
    required this.editorAvailable,
    required this.focusNode,
    required this.onPressed,
  });

  final _ControllerModelView model;
  final bool editorAvailable;
  final FocusNode focusNode;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final enabled = model.connectedUnits == 1 && editorAvailable;
    final unitCopy = model.connectedUnits > 1
        ? '${model.connectedUnits} connected units share this setup.'
        : 'Connected';
    final unavailableCopy = model.connectedUnits > 1
        ? 'Disconnect all but one identical unit before configuring this shared setup.'
        : !editorAvailable
        ? 'Controller setup storage is unavailable.'
        : null;
    final identityCopy = !model.configurable
        ? 'Mapping is unavailable for this connection, but exact input testing is available.'
        : null;
    final semantics = <String>[
      model.displayName,
      model.status,
      unitCopy,
      if (identityCopy != null) identityCopy,
      if (unavailableCopy != null)
        unavailableCopy
      else if (model.configurable)
        'Test or map controller'
      else
        'Test controller',
    ].join('. ');

    return ConsoleSettingsNavigationRow(
      icon: enabled
          ? model.configurable
                ? Icons.gamepad_outlined
                : Icons.sensors
          : Icons.info_outline,
      title: model.displayName,
      subtitle: <String>[
        model.status,
        unitCopy,
        if (identityCopy != null) identityCopy,
        if (unavailableCopy != null) unavailableCopy,
      ].join('  '),
      focusNode: focusNode,
      enabled: enabled,
      semanticLabel: semantics,
      onPressed: onPressed,
    );
  }
}
