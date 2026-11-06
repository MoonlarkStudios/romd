import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

List<String?> controllerDisplayNamesWithOrdinals(Iterable<String?> names) {
  final snapshot = List<String?>.of(names);
  final counts = <String, int>{};
  for (final name in snapshot) {
    if (name != null) {
      counts[name] = (counts[name] ?? 0) + 1;
    }
  }

  final seen = <String, int>{};
  return <String?>[
    for (final name in snapshot)
      if (name == null)
        null
      else if (counts[name] == 1)
        name
      else
        '$name #${seen.update(name, (value) => value + 1, ifAbsent: () => 1)}',
  ];
}

Map<String, String> controllerDisplayNamesById(List<ConnectedGamepad> pads) {
  final labels = controllerDisplayNamesWithOrdinals(<String>[
    for (final pad in pads) pad.name,
  ]);
  return <String, String>{
    for (final (index, pad) in pads.indexed) pad.id: labels[index]!,
  };
}
