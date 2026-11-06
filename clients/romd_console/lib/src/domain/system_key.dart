/// Stable reference identity, independent of server-local numeric IDs and runtime support.
bool isSystemKey(String? value) =>
    value != null &&
    value.length <= 50 &&
    RegExp(r'^[a-z0-9]+(?:[-_][a-z0-9]+)*$').hasMatch(value);
