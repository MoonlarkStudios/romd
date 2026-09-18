import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';

import '../theme/console_theme_context.dart';
import 'console_hint_bar.dart';
import 'controller_setup_workspace.dart';

enum _SetupMode { overview, test, map }

enum _CapturePurpose { test, listen, guided }

enum _DismissChoice { save, discard, continueEditing }

enum _StickCaptureStage { right, down }

/// One device-global workspace for understanding, testing, and editing the
/// physical-to-canonical controller map. Repository writes remain explicit:
/// Save replaces one map atomically; reset removes one custom override.
final class ControllerSetupScreen extends StatefulWidget {
  const ControllerSetupScreen({
    required this.repository,
    required this.descriptor,
    required this.inputProvider,
    required this.connectedUnits,
    super.key,
  });

  final ControllerHardwareMappingRepository repository;
  final RawControllerDescriptor descriptor;
  final Object inputProvider;
  final int connectedUnits;

  @override
  State<ControllerSetupScreen> createState() => _ControllerSetupScreenState();
}

final class _ControllerSetupScreenState extends State<ControllerSetupScreen> {
  static const int _axisCapture = 16000;
  static const int _axisRelease = 8000;
  static const int _axisHold = 12000;
  static const Duration _guidedSkipHold = Duration(milliseconds: 1200);

  final FocusNode _testFocus = FocusNode(debugLabel: 'controller-test');
  final FocusNode _mapFocus = FocusNode(debugLabel: 'controller-map');
  final FocusNode _changeFocus = FocusNode(debugLabel: 'controller-change');
  final FocusNode _stopFocus = FocusNode(debugLabel: 'controller-stop-test');
  final Map<CanonicalGamepadControl, FocusNode> _controlFocus =
      <CanonicalGamepadControl, FocusNode>{};
  final Set<int> _pressedButtons = <int>{};
  final Map<int, int> _axisBaselines = <int, int>{};
  final Map<int, Timer> _holdTimers = <int, Timer>{};
  final Set<CanonicalGamepadControl> _activeControls =
      <CanonicalGamepadControl>{};

  Map<CanonicalGamepadControl, RawGamepadInput> _draft =
      <CanonicalGamepadControl, RawGamepadInput>{};
  Map<CanonicalGamepadControl, RawGamepadInput> _baseline =
      <CanonicalGamepadControl, RawGamepadInput>{};
  ControllerHardwareMapping? _customMapping;
  CanonicalControllerMapping? _detectedMapping;
  ControllerInputCaptureLease? _lease;
  StreamSubscription<RawControllerEvent>? _events;
  _CapturePurpose? _capturePurpose;
  _SetupMode _mode = _SetupMode.overview;
  CanonicalGamepadControl? _selected;
  bool _loading = true;
  bool _saving = false;
  bool _testReturnsToMap = false;
  bool _guided = false;
  String? _status;
  String? _error;
  String? _rawActivity;
  _StickCaptureStage? _stickCaptureStage;
  RawAxisInput? _pendingStickHorizontal;
  int? _pendingStickHorizontalDelta;
  bool _stickRestartRequired = false;
  final Set<int> _stickRestartNeutralAxes = <int>{};

  bool get _hasIdentity => widget.descriptor.sdlGuid?.isNotEmpty == true;
  bool get _dirty => !mapEquals(_draft, _baseline);
  bool get _hasExplicitMappingAction {
    final custom = _customMapping;
    final detected = _detectedMapping;
    if (detected == null) return custom != null;
    return custom == null ||
        !mapEquals(custom.mapping.bindings, detected.bindings);
  }

  bool get _capturing => _capturePurpose != null;
  bool get _listening =>
      _capturePurpose == _CapturePurpose.listen ||
      _capturePurpose == _CapturePurpose.guided;

  String _captureStatus(_CapturePurpose purpose) {
    if (_isStickPair(_selected)) {
      return '${purpose == _CapturePurpose.guided ? 'Guided setup: ' : ''}'
          '${_stickCaptureInstruction()} '
          'Hold any button to ${purpose == _CapturePurpose.guided ? 'skip' : 'cancel'}.';
    }
    return purpose == _CapturePurpose.guided
        ? 'Guided setup: press ${_controlLabel(_selected!)}. '
              'Hold any button to skip.'
        : 'Listening to this controller for one input…';
  }

  String _stickCaptureInstruction() => switch (_stickCaptureStage) {
    _StickCaptureStage.down => 'Keep that stick held right, then move it down.',
    _StickCaptureStage.right || null =>
      'Move the stick you want to use for ${_controlLabel(_selected!)} '
          'to the right.',
  };

  String _describeSelection(CanonicalGamepadControl selected) {
    if (!_isStickPair(selected)) return _describe(_draft[selected]);
    final controls = _stickControls(selected);
    final mappedDirections = controls.where(_draft.containsKey).length;
    return switch (mappedDirections) {
      0 => 'Not mapped',
      1 => 'Incomplete · choose Change to map both directions',
      _ => 'Mapped · directions calibrated',
    };
  }

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _stopCapture(notify: false);
    _testFocus.dispose();
    _mapFocus.dispose();
    _changeFocus.dispose();
    _stopFocus.dispose();
    for (final node in _controlFocus.values) {
      node.dispose();
    }
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final detected = widget.descriptor.sdlMapping;
      if (detected != null) {
        try {
          _detectedMapping = const SdlGamepadMappingCodec()
              .decodeDetected(
                detected,
                fallbackPlatform: widget.descriptor.sdlPlatform,
              )
              .mapping;
        } on Object {
          // Unsupported detected metadata never invalidates Test mode.
        }
      }
      final guid = widget.descriptor.sdlGuid;
      if (guid != null && guid.isNotEmpty) {
        _customMapping = await widget.repository.find(
          sdlPlatform: widget.descriptor.sdlPlatform,
          sdlGuid: guid,
        );
      }
      final mapping = _customMapping?.mapping ?? _detectedMapping;
      _draft = Map<CanonicalGamepadControl, RawGamepadInput>.of(
        mapping?.bindings ?? const <CanonicalGamepadControl, RawGamepadInput>{},
      );
      _baseline = Map<CanonicalGamepadControl, RawGamepadInput>.of(_draft);
      _selected = _editorControl(
        _draft.keys.firstOrNull ?? CanonicalGamepadControl.faceSouth,
      );
    } on Object {
      _error = 'Controller setup could not be loaded. Nothing was changed.';
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _showTest({bool returnToMap = false}) async {
    _guided = false;
    setState(() {
      _mode = _SetupMode.test;
      _testReturnsToMap = returnToMap;
      _status = 'Press controls on this controller.';
      _error = null;
      _rawActivity = null;
    });
    await _startCapture(_CapturePurpose.test);
  }

  void _showMap() {
    _stopCapture();
    setState(() {
      _mode = _SetupMode.map;
      _guided = false;
      _status = 'Select a control on the diagram, then choose Change.';
      _error = null;
    });
    _restoreFocus(_selected);
  }

  Future<void> _startCapture(_CapturePurpose purpose) async {
    _stopCapture(notify: false);
    final result = await acquireControllerCapture(
      widget.inputProvider,
      widget.descriptor.controllerId,
    );
    if (!mounted) {
      if (result case ControllerCaptureStarted(:final lease)) lease.dispose();
      return;
    }
    switch (result) {
      case ControllerCaptureStarted(:final lease):
        _lease = lease;
        _events = lease.events.listen(
          _onRawEvent,
          onError: (_) => _captureFailed(
            'Controller input stopped. Reconnect it and try again.',
          ),
        );
        setState(() {
          _capturePurpose = purpose;
          _axisBaselines.clear();
          _pressedButtons.clear();
          _stickCaptureStage = _isStickPair(_selected)
              ? _StickCaptureStage.right
              : null;
          _pendingStickHorizontal = null;
          _pendingStickHorizontalDelta = null;
          _stickRestartRequired = false;
          _stickRestartNeutralAxes.clear();
          if (purpose != _CapturePurpose.test) {
            _status = _captureStatus(purpose);
          }
        });
      case ControllerCaptureUnsupported():
        setState(() => _error = 'Raw input capture is unavailable.');
      case ControllerCaptureBusy():
        setState(
          () => _error = 'Another controller task is using input capture.',
        );
    }
  }

  void _onRawEvent(RawControllerEvent event) {
    if (!_capturing || event.controllerId != widget.descriptor.controllerId) {
      return;
    }
    switch (event) {
      case RawControllerButtonEvent(:final button, :final pressed):
        if (pressed) {
          _pressedButtons.add(button);
          if (_capturePurpose == _CapturePurpose.guided ||
              _capturePurpose == _CapturePurpose.test ||
              _capturePurpose == _CapturePurpose.listen) {
            _holdTimers[button]?.cancel();
            _holdTimers[button] = Timer(_guidedSkipHold, () {
              if (!mounted || !_pressedButtons.remove(button)) return;
              _holdTimers.remove(button);
              if (_capturePurpose == _CapturePurpose.test) {
                _stopTesting();
              } else if (_capturePurpose == _CapturePurpose.listen) {
                _cancelListening();
              } else {
                _advanceGuide(skipped: true);
              }
            });
          }
          if (_capturePurpose == _CapturePurpose.test) {
            _updateTestButton(button, true);
          }
        } else {
          _holdTimers.remove(button)?.cancel();
          final wasPressed = _pressedButtons.remove(button);
          if (_capturePurpose == _CapturePurpose.test) {
            _updateTestButton(button, false);
          } else if (wasPressed && !_isStickPair(_selected)) {
            _acceptInput(RawButtonInput(button));
          }
        }
      case RawControllerHatEvent(:final hat, :final value):
        if (_capturePurpose == _CapturePurpose.test) {
          _updateTestHat(hat, value);
        } else if (value != 0 && _isStickPair(_selected)) {
          setState(() {
            _status = _stickCaptureInstruction();
            _error = 'Use the stick you want to assign for this change.';
          });
        } else if (value != 0) {
          if (value != 1 && value != 2 && value != 4 && value != 8) {
            setState(() {
              _status = 'Move the D-pad in one direction at a time.';
              _error = null;
            });
          } else {
            _acceptInput(RawHatInput(hat, value));
          }
        }
      case RawControllerAxisEvent(:final axis, :final value):
        final baseline = _axisBaselines.putIfAbsent(axis, () => value);
        final delta = value - baseline;
        if (_capturePurpose == _CapturePurpose.test) {
          _updateTestAxis(axis, delta);
        } else if (_isStickPair(_selected)) {
          _captureStickAxis(axis, delta);
        } else if (delta.abs() >= _axisCapture) {
          final selected = _selected;
          if (selected == null) return;
          final stick = _isStickAxis(selected);
          _acceptInput(
            RawAxisInput(
              axis,
              direction: stick
                  ? RawAxisDirection.full
                  : delta < 0
                  ? RawAxisDirection.negative
                  : RawAxisDirection.positive,
              inverted: stick && delta < 0,
            ),
          );
        }
      case RawControllerDeviceEvent(:final change):
        if (change == RawControllerDeviceChange.removed) {
          _captureFailed('Controller disconnected. Reconnect it to continue.');
        }
    }
  }

  void _updateTestButton(int button, bool pressed) {
    final matches = _controlsFor(RawButtonInput(button));
    setState(() {
      if (pressed) {
        _activeControls.addAll(matches);
      } else {
        _activeControls.removeAll(matches);
      }
      _rawActivity = matches.isEmpty
          ? 'Unmapped button ${button + 1} ${pressed ? 'pressed' : 'released'}'
          : '${matches.map(_controlLabel).join(', ')} ${pressed ? 'pressed' : 'released'}';
    });
  }

  void _updateTestHat(int hat, int value) {
    final mapped = <CanonicalGamepadControl>[];
    for (final entry in _draft.entries) {
      final input = entry.value;
      if (input is RawHatInput && input.hat == hat) {
        mapped.add(entry.key);
        if (value & input.mask != 0) {
          _activeControls.add(entry.key);
        } else {
          _activeControls.remove(entry.key);
        }
      }
    }
    setState(() {
      _rawActivity = mapped.isEmpty
          ? 'Unmapped directional input ${hat + 1}: $value'
          : 'Directional input ${hat + 1}: $value';
    });
  }

  void _updateTestAxis(int axis, int delta) {
    final mapped = <CanonicalGamepadControl>[];
    for (final entry in _draft.entries) {
      final input = entry.value;
      if (input is! RawAxisInput || input.axis != axis) continue;
      mapped.add(entry.key);
      final active = switch (input.direction) {
        RawAxisDirection.full => delta.abs() >= _axisCapture,
        RawAxisDirection.negative => delta <= -_axisCapture,
        RawAxisDirection.positive => delta >= _axisCapture,
      };
      if (active) {
        _activeControls.add(entry.key);
      } else if (delta.abs() <= _axisRelease) {
        _activeControls.remove(entry.key);
      }
    }
    setState(() {
      _rawActivity = mapped.isEmpty
          ? 'Unmapped axis ${axis + 1}: $delta'
          : '${_activityLabels(mapped).join(', ')} '
                '${delta.abs() >= _axisCapture ? 'moved' : 'centered'}';
    });
  }

  Set<String> _activityLabels(Iterable<CanonicalGamepadControl> controls) => {
    for (final control in controls)
      if (_isLeftStickAxis(control))
        'Left stick'
      else if (_isRightStickAxis(control))
        'Right stick'
      else
        _controlLabel(control),
  };

  void _captureStickAxis(int axis, int delta) {
    final selected = _selected;
    final stage = _stickCaptureStage;
    if (selected == null || stage == null) return;
    switch (stage) {
      case _StickCaptureStage.right:
        if (_stickRestartRequired) {
          if (delta.abs() <= _axisRelease) {
            _stickRestartNeutralAxes.add(axis);
            return;
          }
          if (!_stickRestartNeutralAxes.contains(axis)) {
            setState(() {
              _status = 'Return the stick to center, then start again.';
              _error = 'The previous stick gesture was released.';
            });
            return;
          }
        }
        if (delta.abs() < _axisCapture) return;
        final companion = _detectedStickCompanion(axis);
        if (_hasCompleteDetectedStickPair && companion == null) {
          setState(() {
            _status = _stickCaptureInstruction();
            _error = 'That movement is not a detected controller stick.';
          });
          return;
        }
        setState(() {
          _pendingStickHorizontal = RawAxisInput(axis, inverted: delta < 0);
          _pendingStickHorizontalDelta = delta;
          _stickRestartRequired = false;
          _stickRestartNeutralAxes.clear();
          _stickCaptureStage = _StickCaptureStage.down;
          _status = 'Keep that stick held right, then move it down.';
          _error = null;
        });
      case _StickCaptureStage.down:
        final horizontal = _pendingStickHorizontal;
        if (horizontal == null) return;
        if (axis == horizontal.axis) {
          _pendingStickHorizontalDelta = delta;
          if (delta.abs() < _axisHold) {
            setState(() {
              _pendingStickHorizontal = null;
              _pendingStickHorizontalDelta = null;
              _stickRestartRequired = true;
              _stickRestartNeutralAxes
                ..clear()
                ..add(axis);
              _stickCaptureStage = _StickCaptureStage.right;
              _status = 'Return the stick to center, then start again.';
              _error = 'The stick was released. Start the gesture again.';
            });
          }
          return;
        }
        if (delta.abs() < _axisCapture) return;
        if ((_pendingStickHorizontalDelta ?? 0).abs() < _axisHold) {
          setState(() {
            _pendingStickHorizontal = null;
            _pendingStickHorizontalDelta = null;
            _stickRestartRequired = true;
            _stickRestartNeutralAxes.clear();
            _stickCaptureStage = _StickCaptureStage.right;
            _status = 'Return the stick to center, then start again.';
            _error = 'Keep the first direction held while moving down.';
          });
          return;
        }
        final detectedCompanion = _detectedStickCompanion(horizontal.axis);
        if (_hasCompleteDetectedStickPair && axis != detectedCompanion) {
          setState(() {
            _status = 'Keep that stick held right, then move it down.';
            _error = 'That direction belongs to a different controller stick.';
          });
          return;
        }
        _acceptStickPair(horizontal, RawAxisInput(axis, inverted: delta < 0));
    }
  }

  int? _detectedStickCompanion(int horizontalAxis) {
    final detected = _detectedMapping;
    if (detected == null) return null;
    for (final controls in const <List<CanonicalGamepadControl>>[
      <CanonicalGamepadControl>[
        CanonicalGamepadControl.leftStickX,
        CanonicalGamepadControl.leftStickY,
      ],
      <CanonicalGamepadControl>[
        CanonicalGamepadControl.rightStickX,
        CanonicalGamepadControl.rightStickY,
      ],
    ]) {
      final horizontal = detected.inputFor(controls.first);
      final vertical = detected.inputFor(controls.last);
      if (horizontal is RawAxisInput &&
          horizontal.axis == horizontalAxis &&
          vertical is RawAxisInput &&
          vertical.axis != horizontalAxis) {
        return vertical.axis;
      }
    }
    return null;
  }

  bool get _hasCompleteDetectedStickPair {
    final detected = _detectedMapping;
    if (detected == null) return false;
    for (final controls in const <List<CanonicalGamepadControl>>[
      <CanonicalGamepadControl>[
        CanonicalGamepadControl.leftStickX,
        CanonicalGamepadControl.leftStickY,
      ],
      <CanonicalGamepadControl>[
        CanonicalGamepadControl.rightStickX,
        CanonicalGamepadControl.rightStickY,
      ],
    ]) {
      final horizontal = detected.inputFor(controls.first);
      final vertical = detected.inputFor(controls.last);
      if (horizontal is RawAxisInput &&
          vertical is RawAxisInput &&
          horizontal.axis != vertical.axis) {
        return true;
      }
    }
    return false;
  }

  void _acceptStickPair(RawAxisInput horizontal, RawAxisInput vertical) {
    final selected = _selected;
    if (selected == null || !_isStickPair(selected)) return;
    final controls = _stickControls(selected);
    for (final entry in _draft.entries) {
      if (controls.contains(entry.key)) continue;
      final input = entry.value;
      if (input.conflictsWith(horizontal) || input.conflictsWith(vertical)) {
        _stopCapture(notify: false);
        setState(() {
          _error =
              '${_controlLabel(entry.key)} already uses part of that stick. '
              'Nothing was changed.';
          _status = 'Clear the conflicting control or choose another stick.';
        });
        _restoreFocus(selected, changeButton: true);
        return;
      }
    }
    setState(() {
      _draft[controls.first] = horizontal;
      _draft[controls.last] = vertical;
      _error = null;
      _status = '${_controlLabel(selected)} changed as one stick.';
    });
    if (_capturePurpose == _CapturePurpose.guided) {
      _advanceGuide();
    } else {
      _stopCapture();
      _restoreFocus(selected, changeButton: true);
    }
  }

  List<CanonicalGamepadControl> _controlsFor(RawGamepadInput input) =>
      <CanonicalGamepadControl>[
        for (final entry in _draft.entries)
          if (entry.value == input) entry.key,
      ];

  void _acceptInput(RawGamepadInput input) {
    final selected = _selected;
    if (selected == null) return;
    for (final entry in _draft.entries) {
      if (entry.key != selected && entry.value.conflictsWith(input)) {
        _stopCapture(notify: false);
        setState(() {
          _error =
              '${_controlLabel(entry.key)} already uses that input. '
              'Nothing was changed.';
          _status = 'Choose another input or clear the conflicting control.';
        });
        _restoreFocus(selected, changeButton: true);
        return;
      }
    }
    setState(() {
      _draft[selected] = input;
      _error = null;
      _status = '${_controlLabel(selected)} changed to ${_describe(input)}.';
    });
    if (_capturePurpose == _CapturePurpose.guided) {
      _advanceGuide();
    } else {
      _stopCapture();
      _restoreFocus(selected, changeButton: true);
    }
  }

  Future<void> _changeSelected() async {
    if (_selected == null || !_hasIdentity) return;
    _guided = false;
    await _startCapture(_CapturePurpose.listen);
  }

  void _cancelListening() {
    final selected = _selected;
    _stopCapture(notify: false);
    if (!mounted) return;
    setState(() {
      _status = selected == null
          ? 'Change canceled. Nothing was changed.'
          : 'Change canceled. ${_controlLabel(selected)} was not changed.';
      _error = null;
    });
    _restoreFocus(selected, changeButton: true);
  }

  void _clearSelected() {
    final selected = _selected;
    if (selected == null) return;
    setState(() {
      for (final control in _editorControls(selected)) {
        _draft.remove(control);
      }
      _status = '${_controlLabel(selected)} cleared.';
      _error = null;
    });
    _restoreFocus(selected);
  }

  Future<void> _startGuide() async {
    if (!_hasIdentity) return;
    _guided = true;
    setState(() {
      _mode = _SetupMode.map;
      _selected = _guideTargets.first;
      _status = 'Guided setup: press ${_controlLabel(_selected!)}.';
      _error = null;
    });
    await _startCapture(_CapturePurpose.guided);
  }

  void _advanceGuide({bool skipped = false}) {
    final selected = _selected;
    if (selected == null) return;
    final index = _guideTargets.indexOf(selected);
    _stopCapture(notify: false);
    if (index == _guideTargets.length - 1) {
      setState(() {
        _guided = false;
        _status = 'Guided setup finished. Test the draft, then Save.';
      });
      _restoreFocus(selected);
      return;
    }
    final next = _guideTargets[index + 1];
    setState(() {
      _selected = next;
      if (skipped) {
        for (final control in _editorControls(selected)) {
          _draft.remove(control);
        }
      }
      _status = 'Guided setup: press ${_controlLabel(next)}.';
    });
    unawaited(_startCapture(_CapturePurpose.guided));
  }

  void _stopTesting() {
    _stopCapture();
    setState(() {
      _activeControls.clear();
      _rawActivity = null;
      _mode = _testReturnsToMap ? _SetupMode.map : _SetupMode.overview;
      _status = _testReturnsToMap
          ? 'Draft test stopped. Continue editing or Save.'
          : null;
    });
    if (_testReturnsToMap) {
      _restoreFocus(_selected);
    } else {
      _restoreNode(_testFocus);
    }
  }

  void _captureFailed(String message) {
    _stopCapture(notify: false);
    if (mounted) {
      setState(() {
        _guided = false;
        _error = message;
      });
      _restoreFocus(_selected);
    }
  }

  void _stopCapture({bool notify = true}) {
    _events?.cancel();
    _events = null;
    _lease?.dispose();
    _lease = null;
    for (final timer in _holdTimers.values) {
      timer.cancel();
    }
    _holdTimers.clear();
    _pressedButtons.clear();
    _axisBaselines.clear();
    _stickCaptureStage = null;
    _pendingStickHorizontal = null;
    _pendingStickHorizontalDelta = null;
    _stickRestartRequired = false;
    _stickRestartNeutralAxes.clear();
    if (_capturePurpose != null) {
      if (notify && mounted) {
        setState(() => _capturePurpose = null);
      } else {
        _capturePurpose = null;
      }
    }
  }

  Future<bool> _save({bool popAfter = false}) async {
    final guid = widget.descriptor.sdlGuid;
    if (guid == null || guid.isEmpty || _saving) return false;
    _stopCapture();
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final savedMapping = CanonicalControllerMapping(_draft);
      await widget.repository.save(
        sdlPlatform: widget.descriptor.sdlPlatform,
        sdlGuid: guid,
        displayName: widget.descriptor.displayName,
        mapping: savedMapping,
      );
      _customMapping = _mappingState(guid, savedMapping);
      _baseline = Map<CanonicalGamepadControl, RawGamepadInput>.of(_draft);
      if (!mounted) return true;
      if (popAfter) {
        Navigator.of(context).pop();
      } else {
        setState(() {
          _saving = false;
          _status = 'Controller setup saved.';
          _mode = _SetupMode.overview;
        });
        _restoreNode(_testFocus);
      }
      return true;
    } on Object {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = 'Controller setup could not be saved. Your draft is intact.';
        });
      }
      return false;
    }
  }

  Future<void> _resetCustom() async {
    final guid = widget.descriptor.sdlGuid;
    if (guid == null || guid.isEmpty || _saving) return;
    final detected = _detectedMapping != null;
    final replacingCustom = _customMapping != null;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(detected ? 'Use detected setup?' : 'Remove custom setup?'),
        content: Text(
          detected
              ? replacingCustom
                    ? 'This saves the detected controller map as this controller’s setup, replacing the custom map.'
                    : 'This saves the detected controller map as this controller’s setup.'
              : 'This removes the custom map. The controller will need mapping again.',
        ),
        actions: <Widget>[
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(replacingCustom ? 'Keep custom' : 'Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(detected ? 'Use detected' : 'Remove map'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) {
      _restoreNode(_mapFocus);
      return;
    }
    setState(() => _saving = true);
    try {
      final detectedMapping = _detectedMapping;
      if (detectedMapping != null) {
        await widget.repository.save(
          sdlPlatform: widget.descriptor.sdlPlatform,
          sdlGuid: guid,
          displayName: widget.descriptor.displayName,
          mapping: detectedMapping,
        );
      } else {
        await widget.repository.reset(
          sdlPlatform: widget.descriptor.sdlPlatform,
          sdlGuid: guid,
        );
      }
      final restored =
          detectedMapping?.bindings ??
          const <CanonicalGamepadControl, RawGamepadInput>{};
      setState(() {
        _customMapping = detectedMapping == null
            ? null
            : _mappingState(guid, detectedMapping);
        _draft = Map<CanonicalGamepadControl, RawGamepadInput>.of(restored);
        _baseline = Map<CanonicalGamepadControl, RawGamepadInput>.of(restored);
        _saving = false;
        _status = detected ? 'Detected map saved.' : 'Custom map removed.';
        _mode = _SetupMode.overview;
      });
      _restoreNode(_testFocus);
    } on Object {
      setState(() {
        _saving = false;
        _error = detected
            ? 'The detected controller map could not be saved.'
            : 'The custom controller map could not be removed.';
      });
      _restoreNode(_mapFocus);
    }
  }

  ControllerHardwareMapping _mappingState(
    String guid,
    CanonicalControllerMapping mapping,
  ) {
    final now = DateTime.now();
    return ControllerHardwareMapping(
      sdlPlatform: widget.descriptor.sdlPlatform,
      sdlGuid: guid,
      displayName: widget.descriptor.displayName,
      mapping: mapping,
      createdAt: _customMapping?.createdAt ?? now,
      updatedAt: now,
    );
  }

  Future<void> _dismiss() async {
    if (_mode == _SetupMode.test) {
      _stopTesting();
      return;
    }
    if (_capturePurpose == _CapturePurpose.listen) {
      _cancelListening();
      return;
    }
    if (_capturePurpose == _CapturePurpose.guided) {
      _stopCapture();
      setState(() {
        _guided = false;
        _status = 'Guided setup stopped. Your draft is intact.';
      });
      _restoreFocus(_selected);
      return;
    }
    _stopCapture();
    if (!_dirty) {
      Navigator.of(context).pop();
      return;
    }
    final choice = await showDialog<_DismissChoice>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Save controller changes?'),
        content: const Text('Your controller map has unsaved changes.'),
        actions: <Widget>[
          TextButton(
            onPressed: () =>
                Navigator.pop(context, _DismissChoice.continueEditing),
            child: const Text('Continue editing'),
          ),
          TextButton(
            onPressed: () => Navigator.pop(context, _DismissChoice.discard),
            child: const Text('Discard'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, _DismissChoice.save),
            child: const Text('Save changes'),
          ),
        ],
      ),
    );
    if (!mounted) return;
    switch (choice) {
      case _DismissChoice.save:
        await _save(popAfter: true);
      case _DismissChoice.discard:
        Navigator.of(context).pop();
      case _DismissChoice.continueEditing || null:
        _restoreFocus(_selected);
    }
  }

  void _restoreFocus(
    CanonicalGamepadControl? control, {
    bool changeButton = false,
  }) {
    _restoreNode(
      changeButton
          ? _changeFocus
          : control == null
          ? _mapFocus
          : _controlFocus.putIfAbsent(control, FocusNode.new),
    );
  }

  void _restoreNode(FocusNode node) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && node.canRequestFocus) node.requestFocus();
    });
  }

  @override
  Widget build(BuildContext context) => PopScope(
    canPop: false,
    onPopInvokedWithResult: (didPop, _) {
      if (!didPop) _dismiss();
    },
    child: CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.escape): _dismiss,
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              _dismiss();
              return null;
            },
          ),
        },
        child: Scaffold(
          backgroundColor: Colors.transparent,
          body: SafeArea(
            child: _loading
                ? const Center(child: CircularProgressIndicator())
                : _buildBody(),
          ),
        ),
      ),
    ),
  );

  Widget _buildBody() {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;

    return Stack(
      children: <Widget>[
        Positioned.fill(
          bottom: layout.hints.footerMinHeight + layout.lg,
          child: SingleChildScrollView(
            padding: EdgeInsets.symmetric(
              horizontal: layout.lg,
              vertical: layout.md,
            ),
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 900),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: <Widget>[
                    Text(
                      'CONTROLLER SETUP',
                      textAlign: TextAlign.center,
                      style: text.eyebrow,
                    ),
                    Text(
                      widget.descriptor.displayName,
                      textAlign: TextAlign.center,
                      style: text.sectionHeading,
                    ),
                    if (widget.connectedUnits > 1)
                      Text(
                        '${widget.connectedUnits} identical units share this map.',
                        textAlign: TextAlign.center,
                        style: text.metadata.copyWith(color: colors.textMuted),
                      ),
                    if (!_hasIdentity)
                      const ControllerSetupMessagePanel(
                        icon: Icons.info_outline,
                        text:
                            'Mapping unavailable for this connection. You can '
                            'still test exact raw input while it remains connected.',
                      ),
                    if (_error case final error?)
                      ControllerSetupMessagePanel(
                        icon: Icons.error_outline,
                        text: error,
                      ),
                    Semantics(
                      header: true,
                      child: Text(
                        switch (_mode) {
                          _SetupMode.overview => 'OVERVIEW',
                          _SetupMode.test => 'TEST MODE · NO CHANGES ARE SAVED',
                          _SetupMode.map => 'MAP MODE · EDITING A LOCAL DRAFT',
                        },
                        textAlign: TextAlign.center,
                        style: text.metadata.copyWith(color: colors.textMuted),
                      ),
                    ),
                    SizedBox(height: layout.sm),
                    CanonicalControllerDiagram(
                      mapping: _draft,
                      // Selection belongs to the editor. Carrying the default
                      // South selection into Test made a released control look
                      // active even though live activity had cleared correctly.
                      selected: _mode == _SetupMode.map ? _selected : null,
                      active: _activeControls,
                      enabled: _mode == _SetupMode.map && !_capturing,
                      focusNodes: _controlFocus,
                      controlLabel: _controlLabel,
                      onSelected: (control) {
                        setState(() {
                          _selected = control;
                          _status = '${_controlLabel(control)} selected.';
                          _error = null;
                        });
                      },
                    ),
                    SizedBox(height: layout.sm),
                    if (_status case final status?)
                      ControllerSetupMessagePanel(
                        icon: _capturing
                            ? Icons.radio_button_checked
                            : Icons.check_circle_outline,
                        text: status,
                      ),
                    if (_mode == _SetupMode.test) _buildTestControls(),
                    if (_mode == _SetupMode.map) _buildMapControls(),
                    if (_mode == _SetupMode.overview) _buildOverviewControls(),
                  ],
                ),
              ),
            ),
          ),
        ),
        Positioned(
          left: 0,
          right: 0,
          bottom: layout.sm,
          child: Center(
            child: ConsoleHintBar(
              muted: true,
              hints: _capturing
                  ? <ConsoleHint>[
                      const ConsoleHint(
                        glyph: 'Input',
                        gamepadGlyph: 'TARGET',
                        label: 'Listening',
                      ),
                      if (_capturePurpose == _CapturePurpose.test)
                        const ConsoleHint(
                          glyph: 'Hold',
                          gamepadGlyph: 'HOLD',
                          label: 'Stop test',
                        ),
                      if (_capturePurpose == _CapturePurpose.listen)
                        const ConsoleHint(
                          glyph: 'Hold',
                          gamepadGlyph: 'HOLD',
                          label: 'Cancel change',
                        ),
                      if (_capturePurpose == _CapturePurpose.guided)
                        const ConsoleHint(
                          glyph: 'Hold',
                          gamepadGlyph: 'HOLD',
                          label: 'Skip control',
                        ),
                      const ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: '',
                        label: 'Keyboard stop',
                      ),
                    ]
                  : const <ConsoleHint>[
                      ConsoleHint(
                        glyph: '↕',
                        gamepadGlyph: 'D-PAD',
                        label: 'Navigate',
                      ),
                      ConsoleHint(
                        glyph: ConsoleHintGlyphs.confirm,
                        gamepadGlyph: 'A',
                        label: 'Select',
                      ),
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Back',
                      ),
                    ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildOverviewControls() => Padding(
    padding: EdgeInsets.only(top: context.layout.md),
    child: Wrap(
      alignment: WrapAlignment.center,
      spacing: context.layout.sm,
      runSpacing: context.layout.sm,
      children: <Widget>[
        FilledButton.icon(
          focusNode: _testFocus,
          autofocus: true,
          onPressed: () => _showTest(),
          icon: const Icon(Icons.sensors),
          label: const Text('Test controller'),
        ),
        OutlinedButton.icon(
          focusNode: _mapFocus,
          onPressed: _hasIdentity ? _showMap : null,
          icon: const Icon(Icons.tune),
          label: const Text('Map controls'),
        ),
        if (_draft.isEmpty && _hasIdentity)
          OutlinedButton(
            onPressed: _startGuide,
            child: const Text('Guided setup'),
          ),
        if (_hasExplicitMappingAction)
          TextButton(
            onPressed: _saving ? null : _resetCustom,
            child: Text(
              _detectedMapping != null
                  ? 'Use detected map'
                  : 'Remove custom map',
            ),
          ),
        TextButton(onPressed: _dismiss, child: const Text('Back')),
      ],
    ),
  );

  Widget _buildTestControls() => Padding(
    padding: EdgeInsets.only(top: context.layout.sm),
    child: Column(
      children: <Widget>[
        Semantics(
          liveRegion: true,
          child: Text(
            _rawActivity ?? 'Waiting for input from this controller…',
            key: const ValueKey<String>('raw-activity'),
            textAlign: TextAlign.center,
            style: context.text.metadata,
          ),
        ),
        Text(
          'Test mode never changes the map. Hold any button on this controller '
          'for about one second to stop testing.',
          textAlign: TextAlign.center,
          style: context.text.metadata.copyWith(
            color: context.consoleColors.textMuted,
          ),
        ),
        SizedBox(height: context.layout.sm),
        FilledButton(
          focusNode: _stopFocus,
          onPressed: _stopTesting,
          child: Text(_testReturnsToMap ? 'Back to mapping' : 'Stop testing'),
        ),
      ],
    ),
  );

  Widget _buildMapControls() {
    final selected = _selected;
    final hasBinding =
        selected != null && _editorControls(selected).any(_draft.containsKey);
    return Padding(
      padding: EdgeInsets.only(top: context.layout.sm),
      child: Column(
        children: <Widget>[
          Text(
            selected == null
                ? 'Select a control'
                : '${_controlLabel(selected)} · ${_describeSelection(selected)}',
            key: const ValueKey<String>('selected-binding'),
            textAlign: TextAlign.center,
            style: context.text.supportingTitle,
          ),
          if (_listening)
            Text(
              _isStickPair(selected)
                  ? 'Follow the directions above in one continuous movement. '
                        'Hold any button for about one second to '
                        '${_capturePurpose == _CapturePurpose.guided ? 'skip this stick' : 'cancel this change'}.'
                  : _capturePurpose == _CapturePurpose.guided
                  ? 'Press and release to assign. Hold any button for about '
                        'one second to skip this control.'
                  : 'Press and release to assign. Hold any button for about '
                        'one second to cancel this change.',
              textAlign: TextAlign.center,
              style: context.text.metadata.copyWith(
                color: context.consoleColors.textMuted,
              ),
            ),
          SizedBox(height: context.layout.sm),
          Wrap(
            alignment: WrapAlignment.center,
            spacing: context.layout.sm,
            runSpacing: context.layout.sm,
            children: <Widget>[
              FilledButton(
                focusNode: _changeFocus,
                onPressed: _listening ? null : _changeSelected,
                child: Text(_listening ? 'Listening…' : 'Change'),
              ),
              OutlinedButton(
                onPressed: hasBinding && !_capturing ? _clearSelected : null,
                child: const Text('Clear'),
              ),
              OutlinedButton(
                onPressed: _capturing
                    ? null
                    : () => _showTest(returnToMap: true),
                child: const Text('Test draft'),
              ),
              FilledButton.tonal(
                onPressed: _dirty && !_capturing && !_saving ? _save : null,
                child: Text(_saving ? 'Saving…' : 'Save'),
              ),
              if (_guided)
                TextButton(
                  onPressed: () {
                    _stopCapture();
                    setState(() {
                      _guided = false;
                      _status = 'Guided setup stopped. Your draft is intact.';
                    });
                  },
                  child: const Text('Stop guide'),
                ),
              TextButton(onPressed: _dismiss, child: const Text('Cancel')),
            ],
          ),
        ],
      ),
    );
  }
}

const List<CanonicalGamepadControl> _guideTargets = <CanonicalGamepadControl>[
  CanonicalGamepadControl.faceSouth,
  CanonicalGamepadControl.faceEast,
  CanonicalGamepadControl.faceWest,
  CanonicalGamepadControl.faceNorth,
  CanonicalGamepadControl.dpadUp,
  CanonicalGamepadControl.dpadDown,
  CanonicalGamepadControl.dpadLeft,
  CanonicalGamepadControl.dpadRight,
  CanonicalGamepadControl.leftShoulder,
  CanonicalGamepadControl.rightShoulder,
  CanonicalGamepadControl.leftTrigger,
  CanonicalGamepadControl.rightTrigger,
  CanonicalGamepadControl.select,
  CanonicalGamepadControl.start,
  CanonicalGamepadControl.guide,
  CanonicalGamepadControl.leftStickX,
  CanonicalGamepadControl.leftStickPress,
  CanonicalGamepadControl.rightStickX,
  CanonicalGamepadControl.rightStickPress,
];

CanonicalGamepadControl _editorControl(CanonicalGamepadControl control) =>
    switch (control) {
      CanonicalGamepadControl.leftStickY => CanonicalGamepadControl.leftStickX,
      CanonicalGamepadControl.rightStickY =>
        CanonicalGamepadControl.rightStickX,
      _ => control,
    };

bool _isLeftStickAxis(CanonicalGamepadControl control) =>
    control == CanonicalGamepadControl.leftStickX ||
    control == CanonicalGamepadControl.leftStickY;

bool _isRightStickAxis(CanonicalGamepadControl control) =>
    control == CanonicalGamepadControl.rightStickX ||
    control == CanonicalGamepadControl.rightStickY;

bool _isStickAxis(CanonicalGamepadControl control) =>
    _isLeftStickAxis(control) || _isRightStickAxis(control);

bool _isStickPair(CanonicalGamepadControl? control) =>
    control == CanonicalGamepadControl.leftStickX ||
    control == CanonicalGamepadControl.rightStickX;

List<CanonicalGamepadControl> _stickControls(
  CanonicalGamepadControl representative,
) => representative == CanonicalGamepadControl.leftStickX
    ? const <CanonicalGamepadControl>[
        CanonicalGamepadControl.leftStickX,
        CanonicalGamepadControl.leftStickY,
      ]
    : const <CanonicalGamepadControl>[
        CanonicalGamepadControl.rightStickX,
        CanonicalGamepadControl.rightStickY,
      ];

List<CanonicalGamepadControl> _editorControls(
  CanonicalGamepadControl control,
) => _isStickPair(control)
    ? _stickControls(control)
    : <CanonicalGamepadControl>[control];

String _controlLabel(CanonicalGamepadControl control) => switch (control) {
  CanonicalGamepadControl.faceSouth => 'Bottom face button',
  CanonicalGamepadControl.faceEast => 'Right face button',
  CanonicalGamepadControl.faceWest => 'Left face button',
  CanonicalGamepadControl.faceNorth => 'Top face button',
  CanonicalGamepadControl.dpadUp => 'D-pad up',
  CanonicalGamepadControl.dpadDown => 'D-pad down',
  CanonicalGamepadControl.dpadLeft => 'D-pad left',
  CanonicalGamepadControl.dpadRight => 'D-pad right',
  CanonicalGamepadControl.leftShoulder => 'Left shoulder',
  CanonicalGamepadControl.rightShoulder => 'Right shoulder',
  CanonicalGamepadControl.leftTrigger => 'Left trigger',
  CanonicalGamepadControl.rightTrigger => 'Right trigger',
  CanonicalGamepadControl.select => 'Back / Select',
  CanonicalGamepadControl.start => 'Start',
  CanonicalGamepadControl.guide => 'Home / Guide',
  CanonicalGamepadControl.leftStickPress => 'Left stick press',
  CanonicalGamepadControl.rightStickPress => 'Right stick press',
  CanonicalGamepadControl.leftStickX => 'Left stick',
  CanonicalGamepadControl.leftStickY => 'Left stick',
  CanonicalGamepadControl.rightStickX => 'Right stick',
  CanonicalGamepadControl.rightStickY => 'Right stick',
};

String _describe(RawGamepadInput? input) => switch (input) {
  null => 'Not mapped',
  RawButtonInput(:final button) => 'Button ${button + 1}',
  RawHatInput(:final hat, :final mask) =>
    'Directional input ${hat + 1} · $mask',
  RawAxisInput(:final axis, :final direction, :final inverted) =>
    'Axis ${axis + 1} · ${direction.name}${inverted ? ' · inverted' : ''}',
};
