/// Human-readable size for release/cache byte counts, e.g. `512 B`, `3.2 MB`,
/// `12 GB`. One decimal below 10 in a unit, none above — compact enough for the
/// mono metadata register everywhere sizes appear (release strip, cache panel,
/// preservation record).
String formatBytes(int bytes) {
  if (bytes < 1024) {
    return '$bytes B';
  }
  const units = <String>['KB', 'MB', 'GB', 'TB'];
  var value = bytes / 1024.0;
  for (var index = 0; index < units.length; index++) {
    if (value < 1024 || index == units.length - 1) {
      return '${value.toStringAsFixed(value < 10 ? 1 : 0)} ${units[index]}';
    }
    value /= 1024.0;
  }
  return '$bytes B';
}
