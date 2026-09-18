// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'app_database.dart';

// ignore_for_file: type=lint
class $ServerConnectionsTable extends ServerConnections
    with TableInfo<$ServerConnectionsTable, ServerConnectionRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ServerConnectionsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _instanceIdMeta = const VerificationMeta(
    'instanceId',
  );
  @override
  late final GeneratedColumn<String> instanceId = GeneratedColumn<String>(
    'instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastKnownOriginMeta = const VerificationMeta(
    'lastKnownOrigin',
  );
  @override
  late final GeneratedColumn<String> lastKnownOrigin = GeneratedColumn<String>(
    'last_known_origin',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _firstSeenAtMeta = const VerificationMeta(
    'firstSeenAt',
  );
  @override
  late final GeneratedColumn<DateTime> firstSeenAt = GeneratedColumn<DateTime>(
    'first_seen_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastSeenAtMeta = const VerificationMeta(
    'lastSeenAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastSeenAt = GeneratedColumn<DateTime>(
    'last_seen_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    instanceId,
    lastKnownOrigin,
    firstSeenAt,
    lastSeenAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'server_connections';
  @override
  VerificationContext validateIntegrity(
    Insertable<ServerConnectionRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('instance_id')) {
      context.handle(
        _instanceIdMeta,
        instanceId.isAcceptableOrUnknown(data['instance_id']!, _instanceIdMeta),
      );
    } else if (isInserting) {
      context.missing(_instanceIdMeta);
    }
    if (data.containsKey('last_known_origin')) {
      context.handle(
        _lastKnownOriginMeta,
        lastKnownOrigin.isAcceptableOrUnknown(
          data['last_known_origin']!,
          _lastKnownOriginMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastKnownOriginMeta);
    }
    if (data.containsKey('first_seen_at')) {
      context.handle(
        _firstSeenAtMeta,
        firstSeenAt.isAcceptableOrUnknown(
          data['first_seen_at']!,
          _firstSeenAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_firstSeenAtMeta);
    }
    if (data.containsKey('last_seen_at')) {
      context.handle(
        _lastSeenAtMeta,
        lastSeenAt.isAcceptableOrUnknown(
          data['last_seen_at']!,
          _lastSeenAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastSeenAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {instanceId};
  @override
  ServerConnectionRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ServerConnectionRow(
      instanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}instance_id'],
      )!,
      lastKnownOrigin: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}last_known_origin'],
      )!,
      firstSeenAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}first_seen_at'],
      )!,
      lastSeenAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_seen_at'],
      )!,
    );
  }

  @override
  $ServerConnectionsTable createAlias(String alias) {
    return $ServerConnectionsTable(attachedDatabase, alias);
  }
}

class ServerConnectionRow extends DataClass
    implements Insertable<ServerConnectionRow> {
  final String instanceId;
  final String lastKnownOrigin;
  final DateTime firstSeenAt;
  final DateTime lastSeenAt;
  const ServerConnectionRow({
    required this.instanceId,
    required this.lastKnownOrigin,
    required this.firstSeenAt,
    required this.lastSeenAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['instance_id'] = Variable<String>(instanceId);
    map['last_known_origin'] = Variable<String>(lastKnownOrigin);
    map['first_seen_at'] = Variable<DateTime>(firstSeenAt);
    map['last_seen_at'] = Variable<DateTime>(lastSeenAt);
    return map;
  }

  ServerConnectionsCompanion toCompanion(bool nullToAbsent) {
    return ServerConnectionsCompanion(
      instanceId: Value(instanceId),
      lastKnownOrigin: Value(lastKnownOrigin),
      firstSeenAt: Value(firstSeenAt),
      lastSeenAt: Value(lastSeenAt),
    );
  }

  factory ServerConnectionRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ServerConnectionRow(
      instanceId: serializer.fromJson<String>(json['instanceId']),
      lastKnownOrigin: serializer.fromJson<String>(json['lastKnownOrigin']),
      firstSeenAt: serializer.fromJson<DateTime>(json['firstSeenAt']),
      lastSeenAt: serializer.fromJson<DateTime>(json['lastSeenAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'instanceId': serializer.toJson<String>(instanceId),
      'lastKnownOrigin': serializer.toJson<String>(lastKnownOrigin),
      'firstSeenAt': serializer.toJson<DateTime>(firstSeenAt),
      'lastSeenAt': serializer.toJson<DateTime>(lastSeenAt),
    };
  }

  ServerConnectionRow copyWith({
    String? instanceId,
    String? lastKnownOrigin,
    DateTime? firstSeenAt,
    DateTime? lastSeenAt,
  }) => ServerConnectionRow(
    instanceId: instanceId ?? this.instanceId,
    lastKnownOrigin: lastKnownOrigin ?? this.lastKnownOrigin,
    firstSeenAt: firstSeenAt ?? this.firstSeenAt,
    lastSeenAt: lastSeenAt ?? this.lastSeenAt,
  );
  ServerConnectionRow copyWithCompanion(ServerConnectionsCompanion data) {
    return ServerConnectionRow(
      instanceId: data.instanceId.present
          ? data.instanceId.value
          : this.instanceId,
      lastKnownOrigin: data.lastKnownOrigin.present
          ? data.lastKnownOrigin.value
          : this.lastKnownOrigin,
      firstSeenAt: data.firstSeenAt.present
          ? data.firstSeenAt.value
          : this.firstSeenAt,
      lastSeenAt: data.lastSeenAt.present
          ? data.lastSeenAt.value
          : this.lastSeenAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ServerConnectionRow(')
          ..write('instanceId: $instanceId, ')
          ..write('lastKnownOrigin: $lastKnownOrigin, ')
          ..write('firstSeenAt: $firstSeenAt, ')
          ..write('lastSeenAt: $lastSeenAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode =>
      Object.hash(instanceId, lastKnownOrigin, firstSeenAt, lastSeenAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ServerConnectionRow &&
          other.instanceId == this.instanceId &&
          other.lastKnownOrigin == this.lastKnownOrigin &&
          other.firstSeenAt == this.firstSeenAt &&
          other.lastSeenAt == this.lastSeenAt);
}

class ServerConnectionsCompanion extends UpdateCompanion<ServerConnectionRow> {
  final Value<String> instanceId;
  final Value<String> lastKnownOrigin;
  final Value<DateTime> firstSeenAt;
  final Value<DateTime> lastSeenAt;
  final Value<int> rowid;
  const ServerConnectionsCompanion({
    this.instanceId = const Value.absent(),
    this.lastKnownOrigin = const Value.absent(),
    this.firstSeenAt = const Value.absent(),
    this.lastSeenAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ServerConnectionsCompanion.insert({
    required String instanceId,
    required String lastKnownOrigin,
    required DateTime firstSeenAt,
    required DateTime lastSeenAt,
    this.rowid = const Value.absent(),
  }) : instanceId = Value(instanceId),
       lastKnownOrigin = Value(lastKnownOrigin),
       firstSeenAt = Value(firstSeenAt),
       lastSeenAt = Value(lastSeenAt);
  static Insertable<ServerConnectionRow> custom({
    Expression<String>? instanceId,
    Expression<String>? lastKnownOrigin,
    Expression<DateTime>? firstSeenAt,
    Expression<DateTime>? lastSeenAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (instanceId != null) 'instance_id': instanceId,
      if (lastKnownOrigin != null) 'last_known_origin': lastKnownOrigin,
      if (firstSeenAt != null) 'first_seen_at': firstSeenAt,
      if (lastSeenAt != null) 'last_seen_at': lastSeenAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ServerConnectionsCompanion copyWith({
    Value<String>? instanceId,
    Value<String>? lastKnownOrigin,
    Value<DateTime>? firstSeenAt,
    Value<DateTime>? lastSeenAt,
    Value<int>? rowid,
  }) {
    return ServerConnectionsCompanion(
      instanceId: instanceId ?? this.instanceId,
      lastKnownOrigin: lastKnownOrigin ?? this.lastKnownOrigin,
      firstSeenAt: firstSeenAt ?? this.firstSeenAt,
      lastSeenAt: lastSeenAt ?? this.lastSeenAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (instanceId.present) {
      map['instance_id'] = Variable<String>(instanceId.value);
    }
    if (lastKnownOrigin.present) {
      map['last_known_origin'] = Variable<String>(lastKnownOrigin.value);
    }
    if (firstSeenAt.present) {
      map['first_seen_at'] = Variable<DateTime>(firstSeenAt.value);
    }
    if (lastSeenAt.present) {
      map['last_seen_at'] = Variable<DateTime>(lastSeenAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ServerConnectionsCompanion(')
          ..write('instanceId: $instanceId, ')
          ..write('lastKnownOrigin: $lastKnownOrigin, ')
          ..write('firstSeenAt: $firstSeenAt, ')
          ..write('lastSeenAt: $lastSeenAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $LocalProfilesTable extends LocalProfiles
    with TableInfo<$LocalProfilesTable, LocalProfileRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $LocalProfilesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<String> id = GeneratedColumn<String>(
    'id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _displayNameMeta = const VerificationMeta(
    'displayName',
  );
  @override
  late final GeneratedColumn<String> displayName = GeneratedColumn<String>(
    'display_name',
    aliasedName,
    false,
    additionalChecks: GeneratedColumn.checkTextLength(
      minTextLength: 1,
      maxTextLength: 64,
    ),
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _avatarKeyMeta = const VerificationMeta(
    'avatarKey',
  );
  @override
  late final GeneratedColumn<String> avatarKey = GeneratedColumn<String>(
    'avatar_key',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _accentColorMeta = const VerificationMeta(
    'accentColor',
  );
  @override
  late final GeneratedColumn<int> accentColor = GeneratedColumn<int>(
    'accent_color',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _romdServerOriginMeta = const VerificationMeta(
    'romdServerOrigin',
  );
  @override
  late final GeneratedColumn<String> romdServerOrigin = GeneratedColumn<String>(
    'romd_server_origin',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(RomdServerOrigins.defaultValue),
  );
  static const VerificationMeta _entryModeMeta = const VerificationMeta(
    'entryMode',
  );
  @override
  late final GeneratedColumn<String> entryMode = GeneratedColumn<String>(
    'entry_mode',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastUsedAtMeta = const VerificationMeta(
    'lastUsedAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastUsedAt = GeneratedColumn<DateTime>(
    'last_used_at',
    aliasedName,
    true,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _selectedServerInstanceIdMeta =
      const VerificationMeta('selectedServerInstanceId');
  @override
  late final GeneratedColumn<String> selectedServerInstanceId =
      GeneratedColumn<String>(
        'selected_server_instance_id',
        aliasedName,
        true,
        type: DriftSqlType.string,
        requiredDuringInsert: false,
        defaultConstraints: GeneratedColumn.constraintIsAlways(
          'REFERENCES server_connections (instance_id)',
        ),
      );
  static const VerificationMeta _serverSelectionGenerationMeta =
      const VerificationMeta('serverSelectionGeneration');
  @override
  late final GeneratedColumn<int> serverSelectionGeneration =
      GeneratedColumn<int>(
        'server_selection_generation',
        aliasedName,
        false,
        check: () =>
            ComparableExpr(serverSelectionGeneration).isBiggerOrEqualValue(0),
        type: DriftSqlType.int,
        requiredDuringInsert: false,
        defaultValue: const Constant(0),
      );
  @override
  List<GeneratedColumn> get $columns => [
    id,
    displayName,
    avatarKey,
    accentColor,
    romdServerOrigin,
    entryMode,
    createdAt,
    updatedAt,
    lastUsedAt,
    selectedServerInstanceId,
    serverSelectionGeneration,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'local_profiles';
  @override
  VerificationContext validateIntegrity(
    Insertable<LocalProfileRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    } else if (isInserting) {
      context.missing(_idMeta);
    }
    if (data.containsKey('display_name')) {
      context.handle(
        _displayNameMeta,
        displayName.isAcceptableOrUnknown(
          data['display_name']!,
          _displayNameMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_displayNameMeta);
    }
    if (data.containsKey('avatar_key')) {
      context.handle(
        _avatarKeyMeta,
        avatarKey.isAcceptableOrUnknown(data['avatar_key']!, _avatarKeyMeta),
      );
    } else if (isInserting) {
      context.missing(_avatarKeyMeta);
    }
    if (data.containsKey('accent_color')) {
      context.handle(
        _accentColorMeta,
        accentColor.isAcceptableOrUnknown(
          data['accent_color']!,
          _accentColorMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_accentColorMeta);
    }
    if (data.containsKey('romd_server_origin')) {
      context.handle(
        _romdServerOriginMeta,
        romdServerOrigin.isAcceptableOrUnknown(
          data['romd_server_origin']!,
          _romdServerOriginMeta,
        ),
      );
    }
    if (data.containsKey('entry_mode')) {
      context.handle(
        _entryModeMeta,
        entryMode.isAcceptableOrUnknown(data['entry_mode']!, _entryModeMeta),
      );
    } else if (isInserting) {
      context.missing(_entryModeMeta);
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    if (data.containsKey('last_used_at')) {
      context.handle(
        _lastUsedAtMeta,
        lastUsedAt.isAcceptableOrUnknown(
          data['last_used_at']!,
          _lastUsedAtMeta,
        ),
      );
    }
    if (data.containsKey('selected_server_instance_id')) {
      context.handle(
        _selectedServerInstanceIdMeta,
        selectedServerInstanceId.isAcceptableOrUnknown(
          data['selected_server_instance_id']!,
          _selectedServerInstanceIdMeta,
        ),
      );
    }
    if (data.containsKey('server_selection_generation')) {
      context.handle(
        _serverSelectionGenerationMeta,
        serverSelectionGeneration.isAcceptableOrUnknown(
          data['server_selection_generation']!,
          _serverSelectionGenerationMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  LocalProfileRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return LocalProfileRow(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}id'],
      )!,
      displayName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}display_name'],
      )!,
      avatarKey: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}avatar_key'],
      )!,
      accentColor: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}accent_color'],
      )!,
      romdServerOrigin: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}romd_server_origin'],
      )!,
      entryMode: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}entry_mode'],
      )!,
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
      lastUsedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_used_at'],
      ),
      selectedServerInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}selected_server_instance_id'],
      ),
      serverSelectionGeneration: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}server_selection_generation'],
      )!,
    );
  }

  @override
  $LocalProfilesTable createAlias(String alias) {
    return $LocalProfilesTable(attachedDatabase, alias);
  }
}

class LocalProfileRow extends DataClass implements Insertable<LocalProfileRow> {
  final String id;
  final String displayName;
  final String avatarKey;
  final int accentColor;
  final String romdServerOrigin;
  final String entryMode;
  final DateTime createdAt;
  final DateTime updatedAt;
  final DateTime? lastUsedAt;
  final String? selectedServerInstanceId;
  final int serverSelectionGeneration;
  const LocalProfileRow({
    required this.id,
    required this.displayName,
    required this.avatarKey,
    required this.accentColor,
    required this.romdServerOrigin,
    required this.entryMode,
    required this.createdAt,
    required this.updatedAt,
    this.lastUsedAt,
    this.selectedServerInstanceId,
    required this.serverSelectionGeneration,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<String>(id);
    map['display_name'] = Variable<String>(displayName);
    map['avatar_key'] = Variable<String>(avatarKey);
    map['accent_color'] = Variable<int>(accentColor);
    map['romd_server_origin'] = Variable<String>(romdServerOrigin);
    map['entry_mode'] = Variable<String>(entryMode);
    map['created_at'] = Variable<DateTime>(createdAt);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    if (!nullToAbsent || lastUsedAt != null) {
      map['last_used_at'] = Variable<DateTime>(lastUsedAt);
    }
    if (!nullToAbsent || selectedServerInstanceId != null) {
      map['selected_server_instance_id'] = Variable<String>(
        selectedServerInstanceId,
      );
    }
    map['server_selection_generation'] = Variable<int>(
      serverSelectionGeneration,
    );
    return map;
  }

  LocalProfilesCompanion toCompanion(bool nullToAbsent) {
    return LocalProfilesCompanion(
      id: Value(id),
      displayName: Value(displayName),
      avatarKey: Value(avatarKey),
      accentColor: Value(accentColor),
      romdServerOrigin: Value(romdServerOrigin),
      entryMode: Value(entryMode),
      createdAt: Value(createdAt),
      updatedAt: Value(updatedAt),
      lastUsedAt: lastUsedAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastUsedAt),
      selectedServerInstanceId: selectedServerInstanceId == null && nullToAbsent
          ? const Value.absent()
          : Value(selectedServerInstanceId),
      serverSelectionGeneration: Value(serverSelectionGeneration),
    );
  }

  factory LocalProfileRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return LocalProfileRow(
      id: serializer.fromJson<String>(json['id']),
      displayName: serializer.fromJson<String>(json['displayName']),
      avatarKey: serializer.fromJson<String>(json['avatarKey']),
      accentColor: serializer.fromJson<int>(json['accentColor']),
      romdServerOrigin: serializer.fromJson<String>(json['romdServerOrigin']),
      entryMode: serializer.fromJson<String>(json['entryMode']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
      lastUsedAt: serializer.fromJson<DateTime?>(json['lastUsedAt']),
      selectedServerInstanceId: serializer.fromJson<String?>(
        json['selectedServerInstanceId'],
      ),
      serverSelectionGeneration: serializer.fromJson<int>(
        json['serverSelectionGeneration'],
      ),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<String>(id),
      'displayName': serializer.toJson<String>(displayName),
      'avatarKey': serializer.toJson<String>(avatarKey),
      'accentColor': serializer.toJson<int>(accentColor),
      'romdServerOrigin': serializer.toJson<String>(romdServerOrigin),
      'entryMode': serializer.toJson<String>(entryMode),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
      'lastUsedAt': serializer.toJson<DateTime?>(lastUsedAt),
      'selectedServerInstanceId': serializer.toJson<String?>(
        selectedServerInstanceId,
      ),
      'serverSelectionGeneration': serializer.toJson<int>(
        serverSelectionGeneration,
      ),
    };
  }

  LocalProfileRow copyWith({
    String? id,
    String? displayName,
    String? avatarKey,
    int? accentColor,
    String? romdServerOrigin,
    String? entryMode,
    DateTime? createdAt,
    DateTime? updatedAt,
    Value<DateTime?> lastUsedAt = const Value.absent(),
    Value<String?> selectedServerInstanceId = const Value.absent(),
    int? serverSelectionGeneration,
  }) => LocalProfileRow(
    id: id ?? this.id,
    displayName: displayName ?? this.displayName,
    avatarKey: avatarKey ?? this.avatarKey,
    accentColor: accentColor ?? this.accentColor,
    romdServerOrigin: romdServerOrigin ?? this.romdServerOrigin,
    entryMode: entryMode ?? this.entryMode,
    createdAt: createdAt ?? this.createdAt,
    updatedAt: updatedAt ?? this.updatedAt,
    lastUsedAt: lastUsedAt.present ? lastUsedAt.value : this.lastUsedAt,
    selectedServerInstanceId: selectedServerInstanceId.present
        ? selectedServerInstanceId.value
        : this.selectedServerInstanceId,
    serverSelectionGeneration:
        serverSelectionGeneration ?? this.serverSelectionGeneration,
  );
  LocalProfileRow copyWithCompanion(LocalProfilesCompanion data) {
    return LocalProfileRow(
      id: data.id.present ? data.id.value : this.id,
      displayName: data.displayName.present
          ? data.displayName.value
          : this.displayName,
      avatarKey: data.avatarKey.present ? data.avatarKey.value : this.avatarKey,
      accentColor: data.accentColor.present
          ? data.accentColor.value
          : this.accentColor,
      romdServerOrigin: data.romdServerOrigin.present
          ? data.romdServerOrigin.value
          : this.romdServerOrigin,
      entryMode: data.entryMode.present ? data.entryMode.value : this.entryMode,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
      lastUsedAt: data.lastUsedAt.present
          ? data.lastUsedAt.value
          : this.lastUsedAt,
      selectedServerInstanceId: data.selectedServerInstanceId.present
          ? data.selectedServerInstanceId.value
          : this.selectedServerInstanceId,
      serverSelectionGeneration: data.serverSelectionGeneration.present
          ? data.serverSelectionGeneration.value
          : this.serverSelectionGeneration,
    );
  }

  @override
  String toString() {
    return (StringBuffer('LocalProfileRow(')
          ..write('id: $id, ')
          ..write('displayName: $displayName, ')
          ..write('avatarKey: $avatarKey, ')
          ..write('accentColor: $accentColor, ')
          ..write('romdServerOrigin: $romdServerOrigin, ')
          ..write('entryMode: $entryMode, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('lastUsedAt: $lastUsedAt, ')
          ..write('selectedServerInstanceId: $selectedServerInstanceId, ')
          ..write('serverSelectionGeneration: $serverSelectionGeneration')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    id,
    displayName,
    avatarKey,
    accentColor,
    romdServerOrigin,
    entryMode,
    createdAt,
    updatedAt,
    lastUsedAt,
    selectedServerInstanceId,
    serverSelectionGeneration,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is LocalProfileRow &&
          other.id == this.id &&
          other.displayName == this.displayName &&
          other.avatarKey == this.avatarKey &&
          other.accentColor == this.accentColor &&
          other.romdServerOrigin == this.romdServerOrigin &&
          other.entryMode == this.entryMode &&
          other.createdAt == this.createdAt &&
          other.updatedAt == this.updatedAt &&
          other.lastUsedAt == this.lastUsedAt &&
          other.selectedServerInstanceId == this.selectedServerInstanceId &&
          other.serverSelectionGeneration == this.serverSelectionGeneration);
}

class LocalProfilesCompanion extends UpdateCompanion<LocalProfileRow> {
  final Value<String> id;
  final Value<String> displayName;
  final Value<String> avatarKey;
  final Value<int> accentColor;
  final Value<String> romdServerOrigin;
  final Value<String> entryMode;
  final Value<DateTime> createdAt;
  final Value<DateTime> updatedAt;
  final Value<DateTime?> lastUsedAt;
  final Value<String?> selectedServerInstanceId;
  final Value<int> serverSelectionGeneration;
  final Value<int> rowid;
  const LocalProfilesCompanion({
    this.id = const Value.absent(),
    this.displayName = const Value.absent(),
    this.avatarKey = const Value.absent(),
    this.accentColor = const Value.absent(),
    this.romdServerOrigin = const Value.absent(),
    this.entryMode = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.lastUsedAt = const Value.absent(),
    this.selectedServerInstanceId = const Value.absent(),
    this.serverSelectionGeneration = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  LocalProfilesCompanion.insert({
    required String id,
    required String displayName,
    required String avatarKey,
    required int accentColor,
    this.romdServerOrigin = const Value.absent(),
    required String entryMode,
    required DateTime createdAt,
    required DateTime updatedAt,
    this.lastUsedAt = const Value.absent(),
    this.selectedServerInstanceId = const Value.absent(),
    this.serverSelectionGeneration = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : id = Value(id),
       displayName = Value(displayName),
       avatarKey = Value(avatarKey),
       accentColor = Value(accentColor),
       entryMode = Value(entryMode),
       createdAt = Value(createdAt),
       updatedAt = Value(updatedAt);
  static Insertable<LocalProfileRow> custom({
    Expression<String>? id,
    Expression<String>? displayName,
    Expression<String>? avatarKey,
    Expression<int>? accentColor,
    Expression<String>? romdServerOrigin,
    Expression<String>? entryMode,
    Expression<DateTime>? createdAt,
    Expression<DateTime>? updatedAt,
    Expression<DateTime>? lastUsedAt,
    Expression<String>? selectedServerInstanceId,
    Expression<int>? serverSelectionGeneration,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (displayName != null) 'display_name': displayName,
      if (avatarKey != null) 'avatar_key': avatarKey,
      if (accentColor != null) 'accent_color': accentColor,
      if (romdServerOrigin != null) 'romd_server_origin': romdServerOrigin,
      if (entryMode != null) 'entry_mode': entryMode,
      if (createdAt != null) 'created_at': createdAt,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (lastUsedAt != null) 'last_used_at': lastUsedAt,
      if (selectedServerInstanceId != null)
        'selected_server_instance_id': selectedServerInstanceId,
      if (serverSelectionGeneration != null)
        'server_selection_generation': serverSelectionGeneration,
      if (rowid != null) 'rowid': rowid,
    });
  }

  LocalProfilesCompanion copyWith({
    Value<String>? id,
    Value<String>? displayName,
    Value<String>? avatarKey,
    Value<int>? accentColor,
    Value<String>? romdServerOrigin,
    Value<String>? entryMode,
    Value<DateTime>? createdAt,
    Value<DateTime>? updatedAt,
    Value<DateTime?>? lastUsedAt,
    Value<String?>? selectedServerInstanceId,
    Value<int>? serverSelectionGeneration,
    Value<int>? rowid,
  }) {
    return LocalProfilesCompanion(
      id: id ?? this.id,
      displayName: displayName ?? this.displayName,
      avatarKey: avatarKey ?? this.avatarKey,
      accentColor: accentColor ?? this.accentColor,
      romdServerOrigin: romdServerOrigin ?? this.romdServerOrigin,
      entryMode: entryMode ?? this.entryMode,
      createdAt: createdAt ?? this.createdAt,
      updatedAt: updatedAt ?? this.updatedAt,
      lastUsedAt: lastUsedAt ?? this.lastUsedAt,
      selectedServerInstanceId:
          selectedServerInstanceId ?? this.selectedServerInstanceId,
      serverSelectionGeneration:
          serverSelectionGeneration ?? this.serverSelectionGeneration,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<String>(id.value);
    }
    if (displayName.present) {
      map['display_name'] = Variable<String>(displayName.value);
    }
    if (avatarKey.present) {
      map['avatar_key'] = Variable<String>(avatarKey.value);
    }
    if (accentColor.present) {
      map['accent_color'] = Variable<int>(accentColor.value);
    }
    if (romdServerOrigin.present) {
      map['romd_server_origin'] = Variable<String>(romdServerOrigin.value);
    }
    if (entryMode.present) {
      map['entry_mode'] = Variable<String>(entryMode.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (lastUsedAt.present) {
      map['last_used_at'] = Variable<DateTime>(lastUsedAt.value);
    }
    if (selectedServerInstanceId.present) {
      map['selected_server_instance_id'] = Variable<String>(
        selectedServerInstanceId.value,
      );
    }
    if (serverSelectionGeneration.present) {
      map['server_selection_generation'] = Variable<int>(
        serverSelectionGeneration.value,
      );
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('LocalProfilesCompanion(')
          ..write('id: $id, ')
          ..write('displayName: $displayName, ')
          ..write('avatarKey: $avatarKey, ')
          ..write('accentColor: $accentColor, ')
          ..write('romdServerOrigin: $romdServerOrigin, ')
          ..write('entryMode: $entryMode, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('lastUsedAt: $lastUsedAt, ')
          ..write('selectedServerInstanceId: $selectedServerInstanceId, ')
          ..write('serverSelectionGeneration: $serverSelectionGeneration, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $PendingServerLocatorsTable extends PendingServerLocators
    with TableInfo<$PendingServerLocatorsTable, PendingServerLocatorRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $PendingServerLocatorsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _normalizedOriginMeta = const VerificationMeta(
    'normalizedOrigin',
  );
  @override
  late final GeneratedColumn<String> normalizedOrigin = GeneratedColumn<String>(
    'normalized_origin',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastAttemptAtMeta = const VerificationMeta(
    'lastAttemptAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastAttemptAt =
      GeneratedColumn<DateTime>(
        'last_attempt_at',
        aliasedName,
        true,
        type: DriftSqlType.dateTime,
        requiredDuringInsert: false,
      );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    normalizedOrigin,
    createdAt,
    lastAttemptAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'pending_server_locators';
  @override
  VerificationContext validateIntegrity(
    Insertable<PendingServerLocatorRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('normalized_origin')) {
      context.handle(
        _normalizedOriginMeta,
        normalizedOrigin.isAcceptableOrUnknown(
          data['normalized_origin']!,
          _normalizedOriginMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_normalizedOriginMeta);
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('last_attempt_at')) {
      context.handle(
        _lastAttemptAtMeta,
        lastAttemptAt.isAcceptableOrUnknown(
          data['last_attempt_at']!,
          _lastAttemptAtMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {localProfileId};
  @override
  PendingServerLocatorRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return PendingServerLocatorRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      normalizedOrigin: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}normalized_origin'],
      )!,
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      lastAttemptAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_attempt_at'],
      ),
    );
  }

  @override
  $PendingServerLocatorsTable createAlias(String alias) {
    return $PendingServerLocatorsTable(attachedDatabase, alias);
  }
}

class PendingServerLocatorRow extends DataClass
    implements Insertable<PendingServerLocatorRow> {
  final String localProfileId;
  final String normalizedOrigin;
  final DateTime createdAt;
  final DateTime? lastAttemptAt;
  const PendingServerLocatorRow({
    required this.localProfileId,
    required this.normalizedOrigin,
    required this.createdAt,
    this.lastAttemptAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['normalized_origin'] = Variable<String>(normalizedOrigin);
    map['created_at'] = Variable<DateTime>(createdAt);
    if (!nullToAbsent || lastAttemptAt != null) {
      map['last_attempt_at'] = Variable<DateTime>(lastAttemptAt);
    }
    return map;
  }

  PendingServerLocatorsCompanion toCompanion(bool nullToAbsent) {
    return PendingServerLocatorsCompanion(
      localProfileId: Value(localProfileId),
      normalizedOrigin: Value(normalizedOrigin),
      createdAt: Value(createdAt),
      lastAttemptAt: lastAttemptAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastAttemptAt),
    );
  }

  factory PendingServerLocatorRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return PendingServerLocatorRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      normalizedOrigin: serializer.fromJson<String>(json['normalizedOrigin']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      lastAttemptAt: serializer.fromJson<DateTime?>(json['lastAttemptAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'normalizedOrigin': serializer.toJson<String>(normalizedOrigin),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'lastAttemptAt': serializer.toJson<DateTime?>(lastAttemptAt),
    };
  }

  PendingServerLocatorRow copyWith({
    String? localProfileId,
    String? normalizedOrigin,
    DateTime? createdAt,
    Value<DateTime?> lastAttemptAt = const Value.absent(),
  }) => PendingServerLocatorRow(
    localProfileId: localProfileId ?? this.localProfileId,
    normalizedOrigin: normalizedOrigin ?? this.normalizedOrigin,
    createdAt: createdAt ?? this.createdAt,
    lastAttemptAt: lastAttemptAt.present
        ? lastAttemptAt.value
        : this.lastAttemptAt,
  );
  PendingServerLocatorRow copyWithCompanion(
    PendingServerLocatorsCompanion data,
  ) {
    return PendingServerLocatorRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      normalizedOrigin: data.normalizedOrigin.present
          ? data.normalizedOrigin.value
          : this.normalizedOrigin,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      lastAttemptAt: data.lastAttemptAt.present
          ? data.lastAttemptAt.value
          : this.lastAttemptAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('PendingServerLocatorRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('normalizedOrigin: $normalizedOrigin, ')
          ..write('createdAt: $createdAt, ')
          ..write('lastAttemptAt: $lastAttemptAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode =>
      Object.hash(localProfileId, normalizedOrigin, createdAt, lastAttemptAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is PendingServerLocatorRow &&
          other.localProfileId == this.localProfileId &&
          other.normalizedOrigin == this.normalizedOrigin &&
          other.createdAt == this.createdAt &&
          other.lastAttemptAt == this.lastAttemptAt);
}

class PendingServerLocatorsCompanion
    extends UpdateCompanion<PendingServerLocatorRow> {
  final Value<String> localProfileId;
  final Value<String> normalizedOrigin;
  final Value<DateTime> createdAt;
  final Value<DateTime?> lastAttemptAt;
  final Value<int> rowid;
  const PendingServerLocatorsCompanion({
    this.localProfileId = const Value.absent(),
    this.normalizedOrigin = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.lastAttemptAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  PendingServerLocatorsCompanion.insert({
    required String localProfileId,
    required String normalizedOrigin,
    required DateTime createdAt,
    this.lastAttemptAt = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       normalizedOrigin = Value(normalizedOrigin),
       createdAt = Value(createdAt);
  static Insertable<PendingServerLocatorRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? normalizedOrigin,
    Expression<DateTime>? createdAt,
    Expression<DateTime>? lastAttemptAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (normalizedOrigin != null) 'normalized_origin': normalizedOrigin,
      if (createdAt != null) 'created_at': createdAt,
      if (lastAttemptAt != null) 'last_attempt_at': lastAttemptAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  PendingServerLocatorsCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? normalizedOrigin,
    Value<DateTime>? createdAt,
    Value<DateTime?>? lastAttemptAt,
    Value<int>? rowid,
  }) {
    return PendingServerLocatorsCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      normalizedOrigin: normalizedOrigin ?? this.normalizedOrigin,
      createdAt: createdAt ?? this.createdAt,
      lastAttemptAt: lastAttemptAt ?? this.lastAttemptAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (normalizedOrigin.present) {
      map['normalized_origin'] = Variable<String>(normalizedOrigin.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (lastAttemptAt.present) {
      map['last_attempt_at'] = Variable<DateTime>(lastAttemptAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('PendingServerLocatorsCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('normalizedOrigin: $normalizedOrigin, ')
          ..write('createdAt: $createdAt, ')
          ..write('lastAttemptAt: $lastAttemptAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $RomdAccountLinksTable extends RomdAccountLinks
    with TableInfo<$RomdAccountLinksTable, RomdAccountLinkRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $RomdAccountLinksTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id)',
    ),
  );
  static const VerificationMeta _romdUserIdMeta = const VerificationMeta(
    'romdUserId',
  );
  @override
  late final GeneratedColumn<String> romdUserId = GeneratedColumn<String>(
    'romd_user_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _usernameMeta = const VerificationMeta(
    'username',
  );
  @override
  late final GeneratedColumn<String> username = GeneratedColumn<String>(
    'username',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _emailMeta = const VerificationMeta('email');
  @override
  late final GeneratedColumn<String> email = GeneratedColumn<String>(
    'email',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _linkedAtMeta = const VerificationMeta(
    'linkedAt',
  );
  @override
  late final GeneratedColumn<DateTime> linkedAt = GeneratedColumn<DateTime>(
    'linked_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastLoginAtMeta = const VerificationMeta(
    'lastLoginAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastLoginAt = GeneratedColumn<DateTime>(
    'last_login_at',
    aliasedName,
    true,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    romdUserId,
    username,
    email,
    linkedAt,
    lastLoginAt,
    serverInstanceId,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'romd_account_links';
  @override
  VerificationContext validateIntegrity(
    Insertable<RomdAccountLinkRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('romd_user_id')) {
      context.handle(
        _romdUserIdMeta,
        romdUserId.isAcceptableOrUnknown(
          data['romd_user_id']!,
          _romdUserIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_romdUserIdMeta);
    }
    if (data.containsKey('username')) {
      context.handle(
        _usernameMeta,
        username.isAcceptableOrUnknown(data['username']!, _usernameMeta),
      );
    } else if (isInserting) {
      context.missing(_usernameMeta);
    }
    if (data.containsKey('email')) {
      context.handle(
        _emailMeta,
        email.isAcceptableOrUnknown(data['email']!, _emailMeta),
      );
    } else if (isInserting) {
      context.missing(_emailMeta);
    }
    if (data.containsKey('linked_at')) {
      context.handle(
        _linkedAtMeta,
        linkedAt.isAcceptableOrUnknown(data['linked_at']!, _linkedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_linkedAtMeta);
    }
    if (data.containsKey('last_login_at')) {
      context.handle(
        _lastLoginAtMeta,
        lastLoginAt.isAcceptableOrUnknown(
          data['last_login_at']!,
          _lastLoginAtMeta,
        ),
      );
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {localProfileId};
  @override
  RomdAccountLinkRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return RomdAccountLinkRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      romdUserId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}romd_user_id'],
      )!,
      username: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}username'],
      )!,
      email: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}email'],
      )!,
      linkedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}linked_at'],
      )!,
      lastLoginAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_login_at'],
      ),
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      ),
    );
  }

  @override
  $RomdAccountLinksTable createAlias(String alias) {
    return $RomdAccountLinksTable(attachedDatabase, alias);
  }
}

class RomdAccountLinkRow extends DataClass
    implements Insertable<RomdAccountLinkRow> {
  final String localProfileId;
  final String romdUserId;
  final String username;
  final String email;
  final DateTime linkedAt;
  final DateTime? lastLoginAt;
  final String? serverInstanceId;
  const RomdAccountLinkRow({
    required this.localProfileId,
    required this.romdUserId,
    required this.username,
    required this.email,
    required this.linkedAt,
    this.lastLoginAt,
    this.serverInstanceId,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['romd_user_id'] = Variable<String>(romdUserId);
    map['username'] = Variable<String>(username);
    map['email'] = Variable<String>(email);
    map['linked_at'] = Variable<DateTime>(linkedAt);
    if (!nullToAbsent || lastLoginAt != null) {
      map['last_login_at'] = Variable<DateTime>(lastLoginAt);
    }
    if (!nullToAbsent || serverInstanceId != null) {
      map['server_instance_id'] = Variable<String>(serverInstanceId);
    }
    return map;
  }

  RomdAccountLinksCompanion toCompanion(bool nullToAbsent) {
    return RomdAccountLinksCompanion(
      localProfileId: Value(localProfileId),
      romdUserId: Value(romdUserId),
      username: Value(username),
      email: Value(email),
      linkedAt: Value(linkedAt),
      lastLoginAt: lastLoginAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastLoginAt),
      serverInstanceId: serverInstanceId == null && nullToAbsent
          ? const Value.absent()
          : Value(serverInstanceId),
    );
  }

  factory RomdAccountLinkRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return RomdAccountLinkRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      romdUserId: serializer.fromJson<String>(json['romdUserId']),
      username: serializer.fromJson<String>(json['username']),
      email: serializer.fromJson<String>(json['email']),
      linkedAt: serializer.fromJson<DateTime>(json['linkedAt']),
      lastLoginAt: serializer.fromJson<DateTime?>(json['lastLoginAt']),
      serverInstanceId: serializer.fromJson<String?>(json['serverInstanceId']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'romdUserId': serializer.toJson<String>(romdUserId),
      'username': serializer.toJson<String>(username),
      'email': serializer.toJson<String>(email),
      'linkedAt': serializer.toJson<DateTime>(linkedAt),
      'lastLoginAt': serializer.toJson<DateTime?>(lastLoginAt),
      'serverInstanceId': serializer.toJson<String?>(serverInstanceId),
    };
  }

  RomdAccountLinkRow copyWith({
    String? localProfileId,
    String? romdUserId,
    String? username,
    String? email,
    DateTime? linkedAt,
    Value<DateTime?> lastLoginAt = const Value.absent(),
    Value<String?> serverInstanceId = const Value.absent(),
  }) => RomdAccountLinkRow(
    localProfileId: localProfileId ?? this.localProfileId,
    romdUserId: romdUserId ?? this.romdUserId,
    username: username ?? this.username,
    email: email ?? this.email,
    linkedAt: linkedAt ?? this.linkedAt,
    lastLoginAt: lastLoginAt.present ? lastLoginAt.value : this.lastLoginAt,
    serverInstanceId: serverInstanceId.present
        ? serverInstanceId.value
        : this.serverInstanceId,
  );
  RomdAccountLinkRow copyWithCompanion(RomdAccountLinksCompanion data) {
    return RomdAccountLinkRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      romdUserId: data.romdUserId.present
          ? data.romdUserId.value
          : this.romdUserId,
      username: data.username.present ? data.username.value : this.username,
      email: data.email.present ? data.email.value : this.email,
      linkedAt: data.linkedAt.present ? data.linkedAt.value : this.linkedAt,
      lastLoginAt: data.lastLoginAt.present
          ? data.lastLoginAt.value
          : this.lastLoginAt,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
    );
  }

  @override
  String toString() {
    return (StringBuffer('RomdAccountLinkRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('romdUserId: $romdUserId, ')
          ..write('username: $username, ')
          ..write('email: $email, ')
          ..write('linkedAt: $linkedAt, ')
          ..write('lastLoginAt: $lastLoginAt, ')
          ..write('serverInstanceId: $serverInstanceId')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    localProfileId,
    romdUserId,
    username,
    email,
    linkedAt,
    lastLoginAt,
    serverInstanceId,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is RomdAccountLinkRow &&
          other.localProfileId == this.localProfileId &&
          other.romdUserId == this.romdUserId &&
          other.username == this.username &&
          other.email == this.email &&
          other.linkedAt == this.linkedAt &&
          other.lastLoginAt == this.lastLoginAt &&
          other.serverInstanceId == this.serverInstanceId);
}

class RomdAccountLinksCompanion extends UpdateCompanion<RomdAccountLinkRow> {
  final Value<String> localProfileId;
  final Value<String> romdUserId;
  final Value<String> username;
  final Value<String> email;
  final Value<DateTime> linkedAt;
  final Value<DateTime?> lastLoginAt;
  final Value<String?> serverInstanceId;
  final Value<int> rowid;
  const RomdAccountLinksCompanion({
    this.localProfileId = const Value.absent(),
    this.romdUserId = const Value.absent(),
    this.username = const Value.absent(),
    this.email = const Value.absent(),
    this.linkedAt = const Value.absent(),
    this.lastLoginAt = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  RomdAccountLinksCompanion.insert({
    required String localProfileId,
    required String romdUserId,
    required String username,
    required String email,
    required DateTime linkedAt,
    this.lastLoginAt = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       romdUserId = Value(romdUserId),
       username = Value(username),
       email = Value(email),
       linkedAt = Value(linkedAt);
  static Insertable<RomdAccountLinkRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? romdUserId,
    Expression<String>? username,
    Expression<String>? email,
    Expression<DateTime>? linkedAt,
    Expression<DateTime>? lastLoginAt,
    Expression<String>? serverInstanceId,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (romdUserId != null) 'romd_user_id': romdUserId,
      if (username != null) 'username': username,
      if (email != null) 'email': email,
      if (linkedAt != null) 'linked_at': linkedAt,
      if (lastLoginAt != null) 'last_login_at': lastLoginAt,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (rowid != null) 'rowid': rowid,
    });
  }

  RomdAccountLinksCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? romdUserId,
    Value<String>? username,
    Value<String>? email,
    Value<DateTime>? linkedAt,
    Value<DateTime?>? lastLoginAt,
    Value<String?>? serverInstanceId,
    Value<int>? rowid,
  }) {
    return RomdAccountLinksCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      romdUserId: romdUserId ?? this.romdUserId,
      username: username ?? this.username,
      email: email ?? this.email,
      linkedAt: linkedAt ?? this.linkedAt,
      lastLoginAt: lastLoginAt ?? this.lastLoginAt,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (romdUserId.present) {
      map['romd_user_id'] = Variable<String>(romdUserId.value);
    }
    if (username.present) {
      map['username'] = Variable<String>(username.value);
    }
    if (email.present) {
      map['email'] = Variable<String>(email.value);
    }
    if (linkedAt.present) {
      map['linked_at'] = Variable<DateTime>(linkedAt.value);
    }
    if (lastLoginAt.present) {
      map['last_login_at'] = Variable<DateTime>(lastLoginAt.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('RomdAccountLinksCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('romdUserId: $romdUserId, ')
          ..write('username: $username, ')
          ..write('email: $email, ')
          ..write('linkedAt: $linkedAt, ')
          ..write('lastLoginAt: $lastLoginAt, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ProfileLocalGamesTable extends ProfileLocalGames
    with TableInfo<$ProfileLocalGamesTable, ProfileLocalGameRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ProfileLocalGamesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  static const VerificationMeta _releaseIdMeta = const VerificationMeta(
    'releaseId',
  );
  @override
  late final GeneratedColumn<String> releaseId = GeneratedColumn<String>(
    'release_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleIdMeta = const VerificationMeta(
    'titleId',
  );
  @override
  late final GeneratedColumn<String> titleId = GeneratedColumn<String>(
    'title_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _authorizationStateMeta =
      const VerificationMeta('authorizationState');
  @override
  late final GeneratedColumn<String> authorizationState =
      GeneratedColumn<String>(
        'authorization_state',
        aliasedName,
        false,
        check: () =>
            authorizationState.isIn(const <String>['authorized', 'revoked']),
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _acquiredAtMeta = const VerificationMeta(
    'acquiredAt',
  );
  @override
  late final GeneratedColumn<DateTime> acquiredAt = GeneratedColumn<DateTime>(
    'acquired_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastCheckedAtMeta = const VerificationMeta(
    'lastCheckedAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastCheckedAt =
      GeneratedColumn<DateTime>(
        'last_checked_at',
        aliasedName,
        false,
        type: DriftSqlType.dateTime,
        requiredDuringInsert: true,
      );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    serverInstanceId,
    releaseId,
    titleId,
    authorizationState,
    acquiredAt,
    lastCheckedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'profile_local_games';
  @override
  VerificationContext validateIntegrity(
    Insertable<ProfileLocalGameRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('release_id')) {
      context.handle(
        _releaseIdMeta,
        releaseId.isAcceptableOrUnknown(data['release_id']!, _releaseIdMeta),
      );
    } else if (isInserting) {
      context.missing(_releaseIdMeta);
    }
    if (data.containsKey('title_id')) {
      context.handle(
        _titleIdMeta,
        titleId.isAcceptableOrUnknown(data['title_id']!, _titleIdMeta),
      );
    } else if (isInserting) {
      context.missing(_titleIdMeta);
    }
    if (data.containsKey('authorization_state')) {
      context.handle(
        _authorizationStateMeta,
        authorizationState.isAcceptableOrUnknown(
          data['authorization_state']!,
          _authorizationStateMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_authorizationStateMeta);
    }
    if (data.containsKey('acquired_at')) {
      context.handle(
        _acquiredAtMeta,
        acquiredAt.isAcceptableOrUnknown(data['acquired_at']!, _acquiredAtMeta),
      );
    } else if (isInserting) {
      context.missing(_acquiredAtMeta);
    }
    if (data.containsKey('last_checked_at')) {
      context.handle(
        _lastCheckedAtMeta,
        lastCheckedAt.isAcceptableOrUnknown(
          data['last_checked_at']!,
          _lastCheckedAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastCheckedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {
    localProfileId,
    serverInstanceId,
    releaseId,
  };
  @override
  ProfileLocalGameRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ProfileLocalGameRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      releaseId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_id'],
      )!,
      titleId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_id'],
      )!,
      authorizationState: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}authorization_state'],
      )!,
      acquiredAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}acquired_at'],
      )!,
      lastCheckedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_checked_at'],
      )!,
    );
  }

  @override
  $ProfileLocalGamesTable createAlias(String alias) {
    return $ProfileLocalGamesTable(attachedDatabase, alias);
  }
}

class ProfileLocalGameRow extends DataClass
    implements Insertable<ProfileLocalGameRow> {
  final String localProfileId;
  final String serverInstanceId;
  final String releaseId;
  final String titleId;
  final String authorizationState;
  final DateTime acquiredAt;
  final DateTime lastCheckedAt;
  const ProfileLocalGameRow({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.releaseId,
    required this.titleId,
    required this.authorizationState,
    required this.acquiredAt,
    required this.lastCheckedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['release_id'] = Variable<String>(releaseId);
    map['title_id'] = Variable<String>(titleId);
    map['authorization_state'] = Variable<String>(authorizationState);
    map['acquired_at'] = Variable<DateTime>(acquiredAt);
    map['last_checked_at'] = Variable<DateTime>(lastCheckedAt);
    return map;
  }

  ProfileLocalGamesCompanion toCompanion(bool nullToAbsent) {
    return ProfileLocalGamesCompanion(
      localProfileId: Value(localProfileId),
      serverInstanceId: Value(serverInstanceId),
      releaseId: Value(releaseId),
      titleId: Value(titleId),
      authorizationState: Value(authorizationState),
      acquiredAt: Value(acquiredAt),
      lastCheckedAt: Value(lastCheckedAt),
    );
  }

  factory ProfileLocalGameRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ProfileLocalGameRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      releaseId: serializer.fromJson<String>(json['releaseId']),
      titleId: serializer.fromJson<String>(json['titleId']),
      authorizationState: serializer.fromJson<String>(
        json['authorizationState'],
      ),
      acquiredAt: serializer.fromJson<DateTime>(json['acquiredAt']),
      lastCheckedAt: serializer.fromJson<DateTime>(json['lastCheckedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'releaseId': serializer.toJson<String>(releaseId),
      'titleId': serializer.toJson<String>(titleId),
      'authorizationState': serializer.toJson<String>(authorizationState),
      'acquiredAt': serializer.toJson<DateTime>(acquiredAt),
      'lastCheckedAt': serializer.toJson<DateTime>(lastCheckedAt),
    };
  }

  ProfileLocalGameRow copyWith({
    String? localProfileId,
    String? serverInstanceId,
    String? releaseId,
    String? titleId,
    String? authorizationState,
    DateTime? acquiredAt,
    DateTime? lastCheckedAt,
  }) => ProfileLocalGameRow(
    localProfileId: localProfileId ?? this.localProfileId,
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    releaseId: releaseId ?? this.releaseId,
    titleId: titleId ?? this.titleId,
    authorizationState: authorizationState ?? this.authorizationState,
    acquiredAt: acquiredAt ?? this.acquiredAt,
    lastCheckedAt: lastCheckedAt ?? this.lastCheckedAt,
  );
  ProfileLocalGameRow copyWithCompanion(ProfileLocalGamesCompanion data) {
    return ProfileLocalGameRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      releaseId: data.releaseId.present ? data.releaseId.value : this.releaseId,
      titleId: data.titleId.present ? data.titleId.value : this.titleId,
      authorizationState: data.authorizationState.present
          ? data.authorizationState.value
          : this.authorizationState,
      acquiredAt: data.acquiredAt.present
          ? data.acquiredAt.value
          : this.acquiredAt,
      lastCheckedAt: data.lastCheckedAt.present
          ? data.lastCheckedAt.value
          : this.lastCheckedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ProfileLocalGameRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('authorizationState: $authorizationState, ')
          ..write('acquiredAt: $acquiredAt, ')
          ..write('lastCheckedAt: $lastCheckedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    localProfileId,
    serverInstanceId,
    releaseId,
    titleId,
    authorizationState,
    acquiredAt,
    lastCheckedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ProfileLocalGameRow &&
          other.localProfileId == this.localProfileId &&
          other.serverInstanceId == this.serverInstanceId &&
          other.releaseId == this.releaseId &&
          other.titleId == this.titleId &&
          other.authorizationState == this.authorizationState &&
          other.acquiredAt == this.acquiredAt &&
          other.lastCheckedAt == this.lastCheckedAt);
}

class ProfileLocalGamesCompanion extends UpdateCompanion<ProfileLocalGameRow> {
  final Value<String> localProfileId;
  final Value<String> serverInstanceId;
  final Value<String> releaseId;
  final Value<String> titleId;
  final Value<String> authorizationState;
  final Value<DateTime> acquiredAt;
  final Value<DateTime> lastCheckedAt;
  final Value<int> rowid;
  const ProfileLocalGamesCompanion({
    this.localProfileId = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.releaseId = const Value.absent(),
    this.titleId = const Value.absent(),
    this.authorizationState = const Value.absent(),
    this.acquiredAt = const Value.absent(),
    this.lastCheckedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ProfileLocalGamesCompanion.insert({
    required String localProfileId,
    required String serverInstanceId,
    required String releaseId,
    required String titleId,
    required String authorizationState,
    required DateTime acquiredAt,
    required DateTime lastCheckedAt,
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       serverInstanceId = Value(serverInstanceId),
       releaseId = Value(releaseId),
       titleId = Value(titleId),
       authorizationState = Value(authorizationState),
       acquiredAt = Value(acquiredAt),
       lastCheckedAt = Value(lastCheckedAt);
  static Insertable<ProfileLocalGameRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? serverInstanceId,
    Expression<String>? releaseId,
    Expression<String>? titleId,
    Expression<String>? authorizationState,
    Expression<DateTime>? acquiredAt,
    Expression<DateTime>? lastCheckedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (releaseId != null) 'release_id': releaseId,
      if (titleId != null) 'title_id': titleId,
      if (authorizationState != null) 'authorization_state': authorizationState,
      if (acquiredAt != null) 'acquired_at': acquiredAt,
      if (lastCheckedAt != null) 'last_checked_at': lastCheckedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ProfileLocalGamesCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? serverInstanceId,
    Value<String>? releaseId,
    Value<String>? titleId,
    Value<String>? authorizationState,
    Value<DateTime>? acquiredAt,
    Value<DateTime>? lastCheckedAt,
    Value<int>? rowid,
  }) {
    return ProfileLocalGamesCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      releaseId: releaseId ?? this.releaseId,
      titleId: titleId ?? this.titleId,
      authorizationState: authorizationState ?? this.authorizationState,
      acquiredAt: acquiredAt ?? this.acquiredAt,
      lastCheckedAt: lastCheckedAt ?? this.lastCheckedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (releaseId.present) {
      map['release_id'] = Variable<String>(releaseId.value);
    }
    if (titleId.present) {
      map['title_id'] = Variable<String>(titleId.value);
    }
    if (authorizationState.present) {
      map['authorization_state'] = Variable<String>(authorizationState.value);
    }
    if (acquiredAt.present) {
      map['acquired_at'] = Variable<DateTime>(acquiredAt.value);
    }
    if (lastCheckedAt.present) {
      map['last_checked_at'] = Variable<DateTime>(lastCheckedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ProfileLocalGamesCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('authorizationState: $authorizationState, ')
          ..write('acquiredAt: $acquiredAt, ')
          ..write('lastCheckedAt: $lastCheckedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ProfilePlayHistoriesTable extends ProfilePlayHistories
    with TableInfo<$ProfilePlayHistoriesTable, ProfilePlayHistoryRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ProfilePlayHistoriesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  static const VerificationMeta _titleIdMeta = const VerificationMeta(
    'titleId',
  );
  @override
  late final GeneratedColumn<String> titleId = GeneratedColumn<String>(
    'title_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastReleaseIdMeta = const VerificationMeta(
    'lastReleaseId',
  );
  @override
  late final GeneratedColumn<String> lastReleaseId = GeneratedColumn<String>(
    'last_release_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastPlayedAtMeta = const VerificationMeta(
    'lastPlayedAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastPlayedAt = GeneratedColumn<DateTime>(
    'last_played_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _playCountMeta = const VerificationMeta(
    'playCount',
  );
  @override
  late final GeneratedColumn<int> playCount = GeneratedColumn<int>(
    'play_count',
    aliasedName,
    false,
    check: () => ComparableExpr(playCount).isBiggerOrEqualValue(1),
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    serverInstanceId,
    titleId,
    lastReleaseId,
    lastPlayedAt,
    playCount,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'profile_play_histories';
  @override
  VerificationContext validateIntegrity(
    Insertable<ProfilePlayHistoryRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('title_id')) {
      context.handle(
        _titleIdMeta,
        titleId.isAcceptableOrUnknown(data['title_id']!, _titleIdMeta),
      );
    } else if (isInserting) {
      context.missing(_titleIdMeta);
    }
    if (data.containsKey('last_release_id')) {
      context.handle(
        _lastReleaseIdMeta,
        lastReleaseId.isAcceptableOrUnknown(
          data['last_release_id']!,
          _lastReleaseIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastReleaseIdMeta);
    }
    if (data.containsKey('last_played_at')) {
      context.handle(
        _lastPlayedAtMeta,
        lastPlayedAt.isAcceptableOrUnknown(
          data['last_played_at']!,
          _lastPlayedAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastPlayedAtMeta);
    }
    if (data.containsKey('play_count')) {
      context.handle(
        _playCountMeta,
        playCount.isAcceptableOrUnknown(data['play_count']!, _playCountMeta),
      );
    } else if (isInserting) {
      context.missing(_playCountMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {
    localProfileId,
    serverInstanceId,
    titleId,
  };
  @override
  ProfilePlayHistoryRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ProfilePlayHistoryRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      titleId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_id'],
      )!,
      lastReleaseId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}last_release_id'],
      )!,
      lastPlayedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_played_at'],
      )!,
      playCount: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}play_count'],
      )!,
    );
  }

  @override
  $ProfilePlayHistoriesTable createAlias(String alias) {
    return $ProfilePlayHistoriesTable(attachedDatabase, alias);
  }
}

class ProfilePlayHistoryRow extends DataClass
    implements Insertable<ProfilePlayHistoryRow> {
  final String localProfileId;
  final String serverInstanceId;
  final String titleId;
  final String lastReleaseId;
  final DateTime lastPlayedAt;
  final int playCount;
  const ProfilePlayHistoryRow({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.titleId,
    required this.lastReleaseId,
    required this.lastPlayedAt,
    required this.playCount,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['title_id'] = Variable<String>(titleId);
    map['last_release_id'] = Variable<String>(lastReleaseId);
    map['last_played_at'] = Variable<DateTime>(lastPlayedAt);
    map['play_count'] = Variable<int>(playCount);
    return map;
  }

  ProfilePlayHistoriesCompanion toCompanion(bool nullToAbsent) {
    return ProfilePlayHistoriesCompanion(
      localProfileId: Value(localProfileId),
      serverInstanceId: Value(serverInstanceId),
      titleId: Value(titleId),
      lastReleaseId: Value(lastReleaseId),
      lastPlayedAt: Value(lastPlayedAt),
      playCount: Value(playCount),
    );
  }

  factory ProfilePlayHistoryRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ProfilePlayHistoryRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      titleId: serializer.fromJson<String>(json['titleId']),
      lastReleaseId: serializer.fromJson<String>(json['lastReleaseId']),
      lastPlayedAt: serializer.fromJson<DateTime>(json['lastPlayedAt']),
      playCount: serializer.fromJson<int>(json['playCount']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'titleId': serializer.toJson<String>(titleId),
      'lastReleaseId': serializer.toJson<String>(lastReleaseId),
      'lastPlayedAt': serializer.toJson<DateTime>(lastPlayedAt),
      'playCount': serializer.toJson<int>(playCount),
    };
  }

  ProfilePlayHistoryRow copyWith({
    String? localProfileId,
    String? serverInstanceId,
    String? titleId,
    String? lastReleaseId,
    DateTime? lastPlayedAt,
    int? playCount,
  }) => ProfilePlayHistoryRow(
    localProfileId: localProfileId ?? this.localProfileId,
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    titleId: titleId ?? this.titleId,
    lastReleaseId: lastReleaseId ?? this.lastReleaseId,
    lastPlayedAt: lastPlayedAt ?? this.lastPlayedAt,
    playCount: playCount ?? this.playCount,
  );
  ProfilePlayHistoryRow copyWithCompanion(ProfilePlayHistoriesCompanion data) {
    return ProfilePlayHistoryRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      titleId: data.titleId.present ? data.titleId.value : this.titleId,
      lastReleaseId: data.lastReleaseId.present
          ? data.lastReleaseId.value
          : this.lastReleaseId,
      lastPlayedAt: data.lastPlayedAt.present
          ? data.lastPlayedAt.value
          : this.lastPlayedAt,
      playCount: data.playCount.present ? data.playCount.value : this.playCount,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ProfilePlayHistoryRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('titleId: $titleId, ')
          ..write('lastReleaseId: $lastReleaseId, ')
          ..write('lastPlayedAt: $lastPlayedAt, ')
          ..write('playCount: $playCount')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    localProfileId,
    serverInstanceId,
    titleId,
    lastReleaseId,
    lastPlayedAt,
    playCount,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ProfilePlayHistoryRow &&
          other.localProfileId == this.localProfileId &&
          other.serverInstanceId == this.serverInstanceId &&
          other.titleId == this.titleId &&
          other.lastReleaseId == this.lastReleaseId &&
          other.lastPlayedAt == this.lastPlayedAt &&
          other.playCount == this.playCount);
}

class ProfilePlayHistoriesCompanion
    extends UpdateCompanion<ProfilePlayHistoryRow> {
  final Value<String> localProfileId;
  final Value<String> serverInstanceId;
  final Value<String> titleId;
  final Value<String> lastReleaseId;
  final Value<DateTime> lastPlayedAt;
  final Value<int> playCount;
  final Value<int> rowid;
  const ProfilePlayHistoriesCompanion({
    this.localProfileId = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.titleId = const Value.absent(),
    this.lastReleaseId = const Value.absent(),
    this.lastPlayedAt = const Value.absent(),
    this.playCount = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ProfilePlayHistoriesCompanion.insert({
    required String localProfileId,
    required String serverInstanceId,
    required String titleId,
    required String lastReleaseId,
    required DateTime lastPlayedAt,
    required int playCount,
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       serverInstanceId = Value(serverInstanceId),
       titleId = Value(titleId),
       lastReleaseId = Value(lastReleaseId),
       lastPlayedAt = Value(lastPlayedAt),
       playCount = Value(playCount);
  static Insertable<ProfilePlayHistoryRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? serverInstanceId,
    Expression<String>? titleId,
    Expression<String>? lastReleaseId,
    Expression<DateTime>? lastPlayedAt,
    Expression<int>? playCount,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (titleId != null) 'title_id': titleId,
      if (lastReleaseId != null) 'last_release_id': lastReleaseId,
      if (lastPlayedAt != null) 'last_played_at': lastPlayedAt,
      if (playCount != null) 'play_count': playCount,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ProfilePlayHistoriesCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? serverInstanceId,
    Value<String>? titleId,
    Value<String>? lastReleaseId,
    Value<DateTime>? lastPlayedAt,
    Value<int>? playCount,
    Value<int>? rowid,
  }) {
    return ProfilePlayHistoriesCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      titleId: titleId ?? this.titleId,
      lastReleaseId: lastReleaseId ?? this.lastReleaseId,
      lastPlayedAt: lastPlayedAt ?? this.lastPlayedAt,
      playCount: playCount ?? this.playCount,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (titleId.present) {
      map['title_id'] = Variable<String>(titleId.value);
    }
    if (lastReleaseId.present) {
      map['last_release_id'] = Variable<String>(lastReleaseId.value);
    }
    if (lastPlayedAt.present) {
      map['last_played_at'] = Variable<DateTime>(lastPlayedAt.value);
    }
    if (playCount.present) {
      map['play_count'] = Variable<int>(playCount.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ProfilePlayHistoriesCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('titleId: $titleId, ')
          ..write('lastReleaseId: $lastReleaseId, ')
          ..write('lastPlayedAt: $lastPlayedAt, ')
          ..write('playCount: $playCount, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $PlayActivitySyncPreferencesTable extends PlayActivitySyncPreferences
    with
        TableInfo<
          $PlayActivitySyncPreferencesTable,
          PlayActivitySyncPreferenceRow
        > {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $PlayActivitySyncPreferencesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  static const VerificationMeta _enabledMeta = const VerificationMeta(
    'enabled',
  );
  @override
  late final GeneratedColumn<bool> enabled = GeneratedColumn<bool>(
    'enabled',
    aliasedName,
    false,
    type: DriftSqlType.bool,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'CHECK ("enabled" IN (0, 1))',
    ),
    defaultValue: const Constant(false),
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    serverInstanceId,
    enabled,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'play_activity_sync_preferences';
  @override
  VerificationContext validateIntegrity(
    Insertable<PlayActivitySyncPreferenceRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('enabled')) {
      context.handle(
        _enabledMeta,
        enabled.isAcceptableOrUnknown(data['enabled']!, _enabledMeta),
      );
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {localProfileId, serverInstanceId};
  @override
  PlayActivitySyncPreferenceRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return PlayActivitySyncPreferenceRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      enabled: attachedDatabase.typeMapping.read(
        DriftSqlType.bool,
        data['${effectivePrefix}enabled'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $PlayActivitySyncPreferencesTable createAlias(String alias) {
    return $PlayActivitySyncPreferencesTable(attachedDatabase, alias);
  }
}

class PlayActivitySyncPreferenceRow extends DataClass
    implements Insertable<PlayActivitySyncPreferenceRow> {
  final String localProfileId;
  final String serverInstanceId;
  final bool enabled;
  final DateTime updatedAt;
  const PlayActivitySyncPreferenceRow({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.enabled,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['enabled'] = Variable<bool>(enabled);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  PlayActivitySyncPreferencesCompanion toCompanion(bool nullToAbsent) {
    return PlayActivitySyncPreferencesCompanion(
      localProfileId: Value(localProfileId),
      serverInstanceId: Value(serverInstanceId),
      enabled: Value(enabled),
      updatedAt: Value(updatedAt),
    );
  }

  factory PlayActivitySyncPreferenceRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return PlayActivitySyncPreferenceRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      enabled: serializer.fromJson<bool>(json['enabled']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'enabled': serializer.toJson<bool>(enabled),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  PlayActivitySyncPreferenceRow copyWith({
    String? localProfileId,
    String? serverInstanceId,
    bool? enabled,
    DateTime? updatedAt,
  }) => PlayActivitySyncPreferenceRow(
    localProfileId: localProfileId ?? this.localProfileId,
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    enabled: enabled ?? this.enabled,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  PlayActivitySyncPreferenceRow copyWithCompanion(
    PlayActivitySyncPreferencesCompanion data,
  ) {
    return PlayActivitySyncPreferenceRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      enabled: data.enabled.present ? data.enabled.value : this.enabled,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('PlayActivitySyncPreferenceRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('enabled: $enabled, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode =>
      Object.hash(localProfileId, serverInstanceId, enabled, updatedAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is PlayActivitySyncPreferenceRow &&
          other.localProfileId == this.localProfileId &&
          other.serverInstanceId == this.serverInstanceId &&
          other.enabled == this.enabled &&
          other.updatedAt == this.updatedAt);
}

class PlayActivitySyncPreferencesCompanion
    extends UpdateCompanion<PlayActivitySyncPreferenceRow> {
  final Value<String> localProfileId;
  final Value<String> serverInstanceId;
  final Value<bool> enabled;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const PlayActivitySyncPreferencesCompanion({
    this.localProfileId = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.enabled = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  PlayActivitySyncPreferencesCompanion.insert({
    required String localProfileId,
    required String serverInstanceId,
    this.enabled = const Value.absent(),
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       serverInstanceId = Value(serverInstanceId),
       updatedAt = Value(updatedAt);
  static Insertable<PlayActivitySyncPreferenceRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? serverInstanceId,
    Expression<bool>? enabled,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (enabled != null) 'enabled': enabled,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  PlayActivitySyncPreferencesCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? serverInstanceId,
    Value<bool>? enabled,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return PlayActivitySyncPreferencesCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      enabled: enabled ?? this.enabled,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (enabled.present) {
      map['enabled'] = Variable<bool>(enabled.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('PlayActivitySyncPreferencesCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('enabled: $enabled, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $LocalPlaySessionsTable extends LocalPlaySessions
    with TableInfo<$LocalPlaySessionsTable, LocalPlaySessionRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $LocalPlaySessionsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _sessionIdMeta = const VerificationMeta(
    'sessionId',
  );
  @override
  late final GeneratedColumn<String> sessionId = GeneratedColumn<String>(
    'session_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  static const VerificationMeta _clientIdMeta = const VerificationMeta(
    'clientId',
  );
  @override
  late final GeneratedColumn<String> clientId = GeneratedColumn<String>(
    'client_id',
    aliasedName,
    false,
    additionalChecks: GeneratedColumn.checkTextLength(
      minTextLength: 1,
      maxTextLength: 200,
    ),
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleIdMeta = const VerificationMeta(
    'titleId',
  );
  @override
  late final GeneratedColumn<String> titleId = GeneratedColumn<String>(
    'title_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _releaseIdMeta = const VerificationMeta(
    'releaseId',
  );
  @override
  late final GeneratedColumn<String> releaseId = GeneratedColumn<String>(
    'release_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _startedAtMeta = const VerificationMeta(
    'startedAt',
  );
  @override
  late final GeneratedColumn<DateTime> startedAt = GeneratedColumn<DateTime>(
    'started_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _endedAtMeta = const VerificationMeta(
    'endedAt',
  );
  @override
  late final GeneratedColumn<DateTime> endedAt = GeneratedColumn<DateTime>(
    'ended_at',
    aliasedName,
    true,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _activeDurationSecondsMeta =
      const VerificationMeta('activeDurationSeconds');
  @override
  late final GeneratedColumn<int> activeDurationSeconds = GeneratedColumn<int>(
    'active_duration_seconds',
    aliasedName,
    true,
    check: () =>
        activeDurationSeconds.isNull() |
        (ComparableExpr(activeDurationSeconds).isBiggerOrEqualValue(0) &
            ComparableExpr(
              activeDurationSeconds,
            ).isSmallerOrEqualValue(31622400)),
    type: DriftSqlType.int,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _syncEligibleMeta = const VerificationMeta(
    'syncEligible',
  );
  @override
  late final GeneratedColumn<bool> syncEligible = GeneratedColumn<bool>(
    'sync_eligible',
    aliasedName,
    false,
    type: DriftSqlType.bool,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'CHECK ("sync_eligible" IN (0, 1))',
    ),
    defaultValue: const Constant(false),
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    sessionId,
    localProfileId,
    serverInstanceId,
    clientId,
    titleId,
    releaseId,
    startedAt,
    endedAt,
    activeDurationSeconds,
    syncEligible,
    createdAt,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'local_play_sessions';
  @override
  VerificationContext validateIntegrity(
    Insertable<LocalPlaySessionRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('session_id')) {
      context.handle(
        _sessionIdMeta,
        sessionId.isAcceptableOrUnknown(data['session_id']!, _sessionIdMeta),
      );
    } else if (isInserting) {
      context.missing(_sessionIdMeta);
    }
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('client_id')) {
      context.handle(
        _clientIdMeta,
        clientId.isAcceptableOrUnknown(data['client_id']!, _clientIdMeta),
      );
    } else if (isInserting) {
      context.missing(_clientIdMeta);
    }
    if (data.containsKey('title_id')) {
      context.handle(
        _titleIdMeta,
        titleId.isAcceptableOrUnknown(data['title_id']!, _titleIdMeta),
      );
    } else if (isInserting) {
      context.missing(_titleIdMeta);
    }
    if (data.containsKey('release_id')) {
      context.handle(
        _releaseIdMeta,
        releaseId.isAcceptableOrUnknown(data['release_id']!, _releaseIdMeta),
      );
    } else if (isInserting) {
      context.missing(_releaseIdMeta);
    }
    if (data.containsKey('started_at')) {
      context.handle(
        _startedAtMeta,
        startedAt.isAcceptableOrUnknown(data['started_at']!, _startedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_startedAtMeta);
    }
    if (data.containsKey('ended_at')) {
      context.handle(
        _endedAtMeta,
        endedAt.isAcceptableOrUnknown(data['ended_at']!, _endedAtMeta),
      );
    }
    if (data.containsKey('active_duration_seconds')) {
      context.handle(
        _activeDurationSecondsMeta,
        activeDurationSeconds.isAcceptableOrUnknown(
          data['active_duration_seconds']!,
          _activeDurationSecondsMeta,
        ),
      );
    }
    if (data.containsKey('sync_eligible')) {
      context.handle(
        _syncEligibleMeta,
        syncEligible.isAcceptableOrUnknown(
          data['sync_eligible']!,
          _syncEligibleMeta,
        ),
      );
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {sessionId};
  @override
  LocalPlaySessionRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return LocalPlaySessionRow(
      sessionId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}session_id'],
      )!,
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      clientId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}client_id'],
      )!,
      titleId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_id'],
      )!,
      releaseId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_id'],
      )!,
      startedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}started_at'],
      )!,
      endedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}ended_at'],
      ),
      activeDurationSeconds: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}active_duration_seconds'],
      ),
      syncEligible: attachedDatabase.typeMapping.read(
        DriftSqlType.bool,
        data['${effectivePrefix}sync_eligible'],
      )!,
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $LocalPlaySessionsTable createAlias(String alias) {
    return $LocalPlaySessionsTable(attachedDatabase, alias);
  }
}

class LocalPlaySessionRow extends DataClass
    implements Insertable<LocalPlaySessionRow> {
  final String sessionId;
  final String localProfileId;
  final String serverInstanceId;
  final String clientId;
  final String titleId;
  final String releaseId;
  final DateTime startedAt;
  final DateTime? endedAt;
  final int? activeDurationSeconds;
  final bool syncEligible;
  final DateTime createdAt;
  final DateTime updatedAt;
  const LocalPlaySessionRow({
    required this.sessionId,
    required this.localProfileId,
    required this.serverInstanceId,
    required this.clientId,
    required this.titleId,
    required this.releaseId,
    required this.startedAt,
    this.endedAt,
    this.activeDurationSeconds,
    required this.syncEligible,
    required this.createdAt,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['session_id'] = Variable<String>(sessionId);
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['client_id'] = Variable<String>(clientId);
    map['title_id'] = Variable<String>(titleId);
    map['release_id'] = Variable<String>(releaseId);
    map['started_at'] = Variable<DateTime>(startedAt);
    if (!nullToAbsent || endedAt != null) {
      map['ended_at'] = Variable<DateTime>(endedAt);
    }
    if (!nullToAbsent || activeDurationSeconds != null) {
      map['active_duration_seconds'] = Variable<int>(activeDurationSeconds);
    }
    map['sync_eligible'] = Variable<bool>(syncEligible);
    map['created_at'] = Variable<DateTime>(createdAt);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  LocalPlaySessionsCompanion toCompanion(bool nullToAbsent) {
    return LocalPlaySessionsCompanion(
      sessionId: Value(sessionId),
      localProfileId: Value(localProfileId),
      serverInstanceId: Value(serverInstanceId),
      clientId: Value(clientId),
      titleId: Value(titleId),
      releaseId: Value(releaseId),
      startedAt: Value(startedAt),
      endedAt: endedAt == null && nullToAbsent
          ? const Value.absent()
          : Value(endedAt),
      activeDurationSeconds: activeDurationSeconds == null && nullToAbsent
          ? const Value.absent()
          : Value(activeDurationSeconds),
      syncEligible: Value(syncEligible),
      createdAt: Value(createdAt),
      updatedAt: Value(updatedAt),
    );
  }

  factory LocalPlaySessionRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return LocalPlaySessionRow(
      sessionId: serializer.fromJson<String>(json['sessionId']),
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      clientId: serializer.fromJson<String>(json['clientId']),
      titleId: serializer.fromJson<String>(json['titleId']),
      releaseId: serializer.fromJson<String>(json['releaseId']),
      startedAt: serializer.fromJson<DateTime>(json['startedAt']),
      endedAt: serializer.fromJson<DateTime?>(json['endedAt']),
      activeDurationSeconds: serializer.fromJson<int?>(
        json['activeDurationSeconds'],
      ),
      syncEligible: serializer.fromJson<bool>(json['syncEligible']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'sessionId': serializer.toJson<String>(sessionId),
      'localProfileId': serializer.toJson<String>(localProfileId),
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'clientId': serializer.toJson<String>(clientId),
      'titleId': serializer.toJson<String>(titleId),
      'releaseId': serializer.toJson<String>(releaseId),
      'startedAt': serializer.toJson<DateTime>(startedAt),
      'endedAt': serializer.toJson<DateTime?>(endedAt),
      'activeDurationSeconds': serializer.toJson<int?>(activeDurationSeconds),
      'syncEligible': serializer.toJson<bool>(syncEligible),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  LocalPlaySessionRow copyWith({
    String? sessionId,
    String? localProfileId,
    String? serverInstanceId,
    String? clientId,
    String? titleId,
    String? releaseId,
    DateTime? startedAt,
    Value<DateTime?> endedAt = const Value.absent(),
    Value<int?> activeDurationSeconds = const Value.absent(),
    bool? syncEligible,
    DateTime? createdAt,
    DateTime? updatedAt,
  }) => LocalPlaySessionRow(
    sessionId: sessionId ?? this.sessionId,
    localProfileId: localProfileId ?? this.localProfileId,
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    clientId: clientId ?? this.clientId,
    titleId: titleId ?? this.titleId,
    releaseId: releaseId ?? this.releaseId,
    startedAt: startedAt ?? this.startedAt,
    endedAt: endedAt.present ? endedAt.value : this.endedAt,
    activeDurationSeconds: activeDurationSeconds.present
        ? activeDurationSeconds.value
        : this.activeDurationSeconds,
    syncEligible: syncEligible ?? this.syncEligible,
    createdAt: createdAt ?? this.createdAt,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  LocalPlaySessionRow copyWithCompanion(LocalPlaySessionsCompanion data) {
    return LocalPlaySessionRow(
      sessionId: data.sessionId.present ? data.sessionId.value : this.sessionId,
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      clientId: data.clientId.present ? data.clientId.value : this.clientId,
      titleId: data.titleId.present ? data.titleId.value : this.titleId,
      releaseId: data.releaseId.present ? data.releaseId.value : this.releaseId,
      startedAt: data.startedAt.present ? data.startedAt.value : this.startedAt,
      endedAt: data.endedAt.present ? data.endedAt.value : this.endedAt,
      activeDurationSeconds: data.activeDurationSeconds.present
          ? data.activeDurationSeconds.value
          : this.activeDurationSeconds,
      syncEligible: data.syncEligible.present
          ? data.syncEligible.value
          : this.syncEligible,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('LocalPlaySessionRow(')
          ..write('sessionId: $sessionId, ')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('clientId: $clientId, ')
          ..write('titleId: $titleId, ')
          ..write('releaseId: $releaseId, ')
          ..write('startedAt: $startedAt, ')
          ..write('endedAt: $endedAt, ')
          ..write('activeDurationSeconds: $activeDurationSeconds, ')
          ..write('syncEligible: $syncEligible, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    sessionId,
    localProfileId,
    serverInstanceId,
    clientId,
    titleId,
    releaseId,
    startedAt,
    endedAt,
    activeDurationSeconds,
    syncEligible,
    createdAt,
    updatedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is LocalPlaySessionRow &&
          other.sessionId == this.sessionId &&
          other.localProfileId == this.localProfileId &&
          other.serverInstanceId == this.serverInstanceId &&
          other.clientId == this.clientId &&
          other.titleId == this.titleId &&
          other.releaseId == this.releaseId &&
          other.startedAt == this.startedAt &&
          other.endedAt == this.endedAt &&
          other.activeDurationSeconds == this.activeDurationSeconds &&
          other.syncEligible == this.syncEligible &&
          other.createdAt == this.createdAt &&
          other.updatedAt == this.updatedAt);
}

class LocalPlaySessionsCompanion extends UpdateCompanion<LocalPlaySessionRow> {
  final Value<String> sessionId;
  final Value<String> localProfileId;
  final Value<String> serverInstanceId;
  final Value<String> clientId;
  final Value<String> titleId;
  final Value<String> releaseId;
  final Value<DateTime> startedAt;
  final Value<DateTime?> endedAt;
  final Value<int?> activeDurationSeconds;
  final Value<bool> syncEligible;
  final Value<DateTime> createdAt;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const LocalPlaySessionsCompanion({
    this.sessionId = const Value.absent(),
    this.localProfileId = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.clientId = const Value.absent(),
    this.titleId = const Value.absent(),
    this.releaseId = const Value.absent(),
    this.startedAt = const Value.absent(),
    this.endedAt = const Value.absent(),
    this.activeDurationSeconds = const Value.absent(),
    this.syncEligible = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  LocalPlaySessionsCompanion.insert({
    required String sessionId,
    required String localProfileId,
    required String serverInstanceId,
    required String clientId,
    required String titleId,
    required String releaseId,
    required DateTime startedAt,
    this.endedAt = const Value.absent(),
    this.activeDurationSeconds = const Value.absent(),
    this.syncEligible = const Value.absent(),
    required DateTime createdAt,
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : sessionId = Value(sessionId),
       localProfileId = Value(localProfileId),
       serverInstanceId = Value(serverInstanceId),
       clientId = Value(clientId),
       titleId = Value(titleId),
       releaseId = Value(releaseId),
       startedAt = Value(startedAt),
       createdAt = Value(createdAt),
       updatedAt = Value(updatedAt);
  static Insertable<LocalPlaySessionRow> custom({
    Expression<String>? sessionId,
    Expression<String>? localProfileId,
    Expression<String>? serverInstanceId,
    Expression<String>? clientId,
    Expression<String>? titleId,
    Expression<String>? releaseId,
    Expression<DateTime>? startedAt,
    Expression<DateTime>? endedAt,
    Expression<int>? activeDurationSeconds,
    Expression<bool>? syncEligible,
    Expression<DateTime>? createdAt,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (sessionId != null) 'session_id': sessionId,
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (clientId != null) 'client_id': clientId,
      if (titleId != null) 'title_id': titleId,
      if (releaseId != null) 'release_id': releaseId,
      if (startedAt != null) 'started_at': startedAt,
      if (endedAt != null) 'ended_at': endedAt,
      if (activeDurationSeconds != null)
        'active_duration_seconds': activeDurationSeconds,
      if (syncEligible != null) 'sync_eligible': syncEligible,
      if (createdAt != null) 'created_at': createdAt,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  LocalPlaySessionsCompanion copyWith({
    Value<String>? sessionId,
    Value<String>? localProfileId,
    Value<String>? serverInstanceId,
    Value<String>? clientId,
    Value<String>? titleId,
    Value<String>? releaseId,
    Value<DateTime>? startedAt,
    Value<DateTime?>? endedAt,
    Value<int?>? activeDurationSeconds,
    Value<bool>? syncEligible,
    Value<DateTime>? createdAt,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return LocalPlaySessionsCompanion(
      sessionId: sessionId ?? this.sessionId,
      localProfileId: localProfileId ?? this.localProfileId,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      clientId: clientId ?? this.clientId,
      titleId: titleId ?? this.titleId,
      releaseId: releaseId ?? this.releaseId,
      startedAt: startedAt ?? this.startedAt,
      endedAt: endedAt ?? this.endedAt,
      activeDurationSeconds:
          activeDurationSeconds ?? this.activeDurationSeconds,
      syncEligible: syncEligible ?? this.syncEligible,
      createdAt: createdAt ?? this.createdAt,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (sessionId.present) {
      map['session_id'] = Variable<String>(sessionId.value);
    }
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (clientId.present) {
      map['client_id'] = Variable<String>(clientId.value);
    }
    if (titleId.present) {
      map['title_id'] = Variable<String>(titleId.value);
    }
    if (releaseId.present) {
      map['release_id'] = Variable<String>(releaseId.value);
    }
    if (startedAt.present) {
      map['started_at'] = Variable<DateTime>(startedAt.value);
    }
    if (endedAt.present) {
      map['ended_at'] = Variable<DateTime>(endedAt.value);
    }
    if (activeDurationSeconds.present) {
      map['active_duration_seconds'] = Variable<int>(
        activeDurationSeconds.value,
      );
    }
    if (syncEligible.present) {
      map['sync_eligible'] = Variable<bool>(syncEligible.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('LocalPlaySessionsCompanion(')
          ..write('sessionId: $sessionId, ')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('clientId: $clientId, ')
          ..write('titleId: $titleId, ')
          ..write('releaseId: $releaseId, ')
          ..write('startedAt: $startedAt, ')
          ..write('endedAt: $endedAt, ')
          ..write('activeDurationSeconds: $activeDurationSeconds, ')
          ..write('syncEligible: $syncEligible, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $PlayActivityOutboxTable extends PlayActivityOutbox
    with TableInfo<$PlayActivityOutboxTable, PlayActivityOutboxRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $PlayActivityOutboxTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _sessionIdMeta = const VerificationMeta(
    'sessionId',
  );
  @override
  late final GeneratedColumn<String> sessionId = GeneratedColumn<String>(
    'session_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_play_sessions (session_id) ON DELETE CASCADE',
    ),
  );
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _queuedAtMeta = const VerificationMeta(
    'queuedAt',
  );
  @override
  late final GeneratedColumn<DateTime> queuedAt = GeneratedColumn<DateTime>(
    'queued_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _attemptCountMeta = const VerificationMeta(
    'attemptCount',
  );
  @override
  late final GeneratedColumn<int> attemptCount = GeneratedColumn<int>(
    'attempt_count',
    aliasedName,
    false,
    check: () => ComparableExpr(attemptCount).isBiggerOrEqualValue(0),
    type: DriftSqlType.int,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _nextAttemptAtMeta = const VerificationMeta(
    'nextAttemptAt',
  );
  @override
  late final GeneratedColumn<DateTime> nextAttemptAt =
      GeneratedColumn<DateTime>(
        'next_attempt_at',
        aliasedName,
        false,
        type: DriftSqlType.dateTime,
        requiredDuringInsert: true,
      );
  @override
  List<GeneratedColumn> get $columns => [
    sessionId,
    localProfileId,
    serverInstanceId,
    queuedAt,
    attemptCount,
    nextAttemptAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'play_activity_outbox';
  @override
  VerificationContext validateIntegrity(
    Insertable<PlayActivityOutboxRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('session_id')) {
      context.handle(
        _sessionIdMeta,
        sessionId.isAcceptableOrUnknown(data['session_id']!, _sessionIdMeta),
      );
    } else if (isInserting) {
      context.missing(_sessionIdMeta);
    }
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('queued_at')) {
      context.handle(
        _queuedAtMeta,
        queuedAt.isAcceptableOrUnknown(data['queued_at']!, _queuedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_queuedAtMeta);
    }
    if (data.containsKey('attempt_count')) {
      context.handle(
        _attemptCountMeta,
        attemptCount.isAcceptableOrUnknown(
          data['attempt_count']!,
          _attemptCountMeta,
        ),
      );
    }
    if (data.containsKey('next_attempt_at')) {
      context.handle(
        _nextAttemptAtMeta,
        nextAttemptAt.isAcceptableOrUnknown(
          data['next_attempt_at']!,
          _nextAttemptAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_nextAttemptAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {sessionId};
  @override
  PlayActivityOutboxRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return PlayActivityOutboxRow(
      sessionId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}session_id'],
      )!,
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      queuedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}queued_at'],
      )!,
      attemptCount: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}attempt_count'],
      )!,
      nextAttemptAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}next_attempt_at'],
      )!,
    );
  }

  @override
  $PlayActivityOutboxTable createAlias(String alias) {
    return $PlayActivityOutboxTable(attachedDatabase, alias);
  }
}

class PlayActivityOutboxRow extends DataClass
    implements Insertable<PlayActivityOutboxRow> {
  final String sessionId;
  final String localProfileId;
  final String serverInstanceId;
  final DateTime queuedAt;
  final int attemptCount;
  final DateTime nextAttemptAt;
  const PlayActivityOutboxRow({
    required this.sessionId,
    required this.localProfileId,
    required this.serverInstanceId,
    required this.queuedAt,
    required this.attemptCount,
    required this.nextAttemptAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['session_id'] = Variable<String>(sessionId);
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['queued_at'] = Variable<DateTime>(queuedAt);
    map['attempt_count'] = Variable<int>(attemptCount);
    map['next_attempt_at'] = Variable<DateTime>(nextAttemptAt);
    return map;
  }

  PlayActivityOutboxCompanion toCompanion(bool nullToAbsent) {
    return PlayActivityOutboxCompanion(
      sessionId: Value(sessionId),
      localProfileId: Value(localProfileId),
      serverInstanceId: Value(serverInstanceId),
      queuedAt: Value(queuedAt),
      attemptCount: Value(attemptCount),
      nextAttemptAt: Value(nextAttemptAt),
    );
  }

  factory PlayActivityOutboxRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return PlayActivityOutboxRow(
      sessionId: serializer.fromJson<String>(json['sessionId']),
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      queuedAt: serializer.fromJson<DateTime>(json['queuedAt']),
      attemptCount: serializer.fromJson<int>(json['attemptCount']),
      nextAttemptAt: serializer.fromJson<DateTime>(json['nextAttemptAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'sessionId': serializer.toJson<String>(sessionId),
      'localProfileId': serializer.toJson<String>(localProfileId),
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'queuedAt': serializer.toJson<DateTime>(queuedAt),
      'attemptCount': serializer.toJson<int>(attemptCount),
      'nextAttemptAt': serializer.toJson<DateTime>(nextAttemptAt),
    };
  }

  PlayActivityOutboxRow copyWith({
    String? sessionId,
    String? localProfileId,
    String? serverInstanceId,
    DateTime? queuedAt,
    int? attemptCount,
    DateTime? nextAttemptAt,
  }) => PlayActivityOutboxRow(
    sessionId: sessionId ?? this.sessionId,
    localProfileId: localProfileId ?? this.localProfileId,
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    queuedAt: queuedAt ?? this.queuedAt,
    attemptCount: attemptCount ?? this.attemptCount,
    nextAttemptAt: nextAttemptAt ?? this.nextAttemptAt,
  );
  PlayActivityOutboxRow copyWithCompanion(PlayActivityOutboxCompanion data) {
    return PlayActivityOutboxRow(
      sessionId: data.sessionId.present ? data.sessionId.value : this.sessionId,
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      queuedAt: data.queuedAt.present ? data.queuedAt.value : this.queuedAt,
      attemptCount: data.attemptCount.present
          ? data.attemptCount.value
          : this.attemptCount,
      nextAttemptAt: data.nextAttemptAt.present
          ? data.nextAttemptAt.value
          : this.nextAttemptAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('PlayActivityOutboxRow(')
          ..write('sessionId: $sessionId, ')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('queuedAt: $queuedAt, ')
          ..write('attemptCount: $attemptCount, ')
          ..write('nextAttemptAt: $nextAttemptAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    sessionId,
    localProfileId,
    serverInstanceId,
    queuedAt,
    attemptCount,
    nextAttemptAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is PlayActivityOutboxRow &&
          other.sessionId == this.sessionId &&
          other.localProfileId == this.localProfileId &&
          other.serverInstanceId == this.serverInstanceId &&
          other.queuedAt == this.queuedAt &&
          other.attemptCount == this.attemptCount &&
          other.nextAttemptAt == this.nextAttemptAt);
}

class PlayActivityOutboxCompanion
    extends UpdateCompanion<PlayActivityOutboxRow> {
  final Value<String> sessionId;
  final Value<String> localProfileId;
  final Value<String> serverInstanceId;
  final Value<DateTime> queuedAt;
  final Value<int> attemptCount;
  final Value<DateTime> nextAttemptAt;
  final Value<int> rowid;
  const PlayActivityOutboxCompanion({
    this.sessionId = const Value.absent(),
    this.localProfileId = const Value.absent(),
    this.serverInstanceId = const Value.absent(),
    this.queuedAt = const Value.absent(),
    this.attemptCount = const Value.absent(),
    this.nextAttemptAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  PlayActivityOutboxCompanion.insert({
    required String sessionId,
    required String localProfileId,
    required String serverInstanceId,
    required DateTime queuedAt,
    this.attemptCount = const Value.absent(),
    required DateTime nextAttemptAt,
    this.rowid = const Value.absent(),
  }) : sessionId = Value(sessionId),
       localProfileId = Value(localProfileId),
       serverInstanceId = Value(serverInstanceId),
       queuedAt = Value(queuedAt),
       nextAttemptAt = Value(nextAttemptAt);
  static Insertable<PlayActivityOutboxRow> custom({
    Expression<String>? sessionId,
    Expression<String>? localProfileId,
    Expression<String>? serverInstanceId,
    Expression<DateTime>? queuedAt,
    Expression<int>? attemptCount,
    Expression<DateTime>? nextAttemptAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (sessionId != null) 'session_id': sessionId,
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (queuedAt != null) 'queued_at': queuedAt,
      if (attemptCount != null) 'attempt_count': attemptCount,
      if (nextAttemptAt != null) 'next_attempt_at': nextAttemptAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  PlayActivityOutboxCompanion copyWith({
    Value<String>? sessionId,
    Value<String>? localProfileId,
    Value<String>? serverInstanceId,
    Value<DateTime>? queuedAt,
    Value<int>? attemptCount,
    Value<DateTime>? nextAttemptAt,
    Value<int>? rowid,
  }) {
    return PlayActivityOutboxCompanion(
      sessionId: sessionId ?? this.sessionId,
      localProfileId: localProfileId ?? this.localProfileId,
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      queuedAt: queuedAt ?? this.queuedAt,
      attemptCount: attemptCount ?? this.attemptCount,
      nextAttemptAt: nextAttemptAt ?? this.nextAttemptAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (sessionId.present) {
      map['session_id'] = Variable<String>(sessionId.value);
    }
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (queuedAt.present) {
      map['queued_at'] = Variable<DateTime>(queuedAt.value);
    }
    if (attemptCount.present) {
      map['attempt_count'] = Variable<int>(attemptCount.value);
    }
    if (nextAttemptAt.present) {
      map['next_attempt_at'] = Variable<DateTime>(nextAttemptAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('PlayActivityOutboxCompanion(')
          ..write('sessionId: $sessionId, ')
          ..write('localProfileId: $localProfileId, ')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('queuedAt: $queuedAt, ')
          ..write('attemptCount: $attemptCount, ')
          ..write('nextAttemptAt: $nextAttemptAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $LegacyLocalInstallsTable extends LegacyLocalInstalls
    with TableInfo<$LegacyLocalInstallsTable, LegacyLocalInstallRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $LegacyLocalInstallsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _releaseIdMeta = const VerificationMeta(
    'releaseId',
  );
  @override
  late final GeneratedColumn<String> releaseId = GeneratedColumn<String>(
    'release_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleIdMeta = const VerificationMeta(
    'titleId',
  );
  @override
  late final GeneratedColumn<String> titleId = GeneratedColumn<String>(
    'title_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleNameMeta = const VerificationMeta(
    'titleName',
  );
  @override
  late final GeneratedColumn<String> titleName = GeneratedColumn<String>(
    'title_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _platformIdMeta = const VerificationMeta(
    'platformId',
  );
  @override
  late final GeneratedColumn<String> platformId = GeneratedColumn<String>(
    'platform_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _platformNameMeta = const VerificationMeta(
    'platformName',
  );
  @override
  late final GeneratedColumn<String> platformName = GeneratedColumn<String>(
    'platform_name',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _platformShortNameMeta = const VerificationMeta(
    'platformShortName',
  );
  @override
  late final GeneratedColumn<String> platformShortName =
      GeneratedColumn<String>(
        'platform_short_name',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _coverUrlMeta = const VerificationMeta(
    'coverUrl',
  );
  @override
  late final GeneratedColumn<String> coverUrl = GeneratedColumn<String>(
    'cover_url',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _releaseNameMeta = const VerificationMeta(
    'releaseName',
  );
  @override
  late final GeneratedColumn<String> releaseName = GeneratedColumn<String>(
    'release_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _releaseRevisionMeta = const VerificationMeta(
    'releaseRevision',
  );
  @override
  late final GeneratedColumn<String> releaseRevision = GeneratedColumn<String>(
    'release_revision',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _contentRootMeta = const VerificationMeta(
    'contentRoot',
  );
  @override
  late final GeneratedColumn<String> contentRoot = GeneratedColumn<String>(
    'content_root',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _launchRelativePathMeta =
      const VerificationMeta('launchRelativePath');
  @override
  late final GeneratedColumn<String> launchRelativePath =
      GeneratedColumn<String>(
        'launch_relative_path',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _sizeBytesMeta = const VerificationMeta(
    'sizeBytes',
  );
  @override
  late final GeneratedColumn<int> sizeBytes = GeneratedColumn<int>(
    'size_bytes',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _primarySha256Meta = const VerificationMeta(
    'primarySha256',
  );
  @override
  late final GeneratedColumn<String> primarySha256 = GeneratedColumn<String>(
    'primary_sha256',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _manifestFingerprintMeta =
      const VerificationMeta('manifestFingerprint');
  @override
  late final GeneratedColumn<String> manifestFingerprint =
      GeneratedColumn<String>(
        'manifest_fingerprint',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _stateMeta = const VerificationMeta('state');
  @override
  late final GeneratedColumn<String> state = GeneratedColumn<String>(
    'state',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _installModeMeta = const VerificationMeta(
    'installMode',
  );
  @override
  late final GeneratedColumn<String> installMode = GeneratedColumn<String>(
    'install_mode',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant('permanent'),
  );
  static const VerificationMeta _manifestSnapshotMeta = const VerificationMeta(
    'manifestSnapshot',
  );
  @override
  late final GeneratedColumn<String> manifestSnapshot = GeneratedColumn<String>(
    'manifest_snapshot',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _installedAtMeta = const VerificationMeta(
    'installedAt',
  );
  @override
  late final GeneratedColumn<DateTime> installedAt = GeneratedColumn<DateTime>(
    'installed_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastPlayedAtMeta = const VerificationMeta(
    'lastPlayedAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastPlayedAt = GeneratedColumn<DateTime>(
    'last_played_at',
    aliasedName,
    true,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: false,
  );
  @override
  List<GeneratedColumn> get $columns => [
    releaseId,
    titleId,
    titleName,
    platformId,
    platformName,
    platformShortName,
    coverUrl,
    releaseName,
    releaseRevision,
    contentRoot,
    launchRelativePath,
    sizeBytes,
    primarySha256,
    manifestFingerprint,
    state,
    installMode,
    manifestSnapshot,
    installedAt,
    lastPlayedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'legacy_local_installs';
  @override
  VerificationContext validateIntegrity(
    Insertable<LegacyLocalInstallRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('release_id')) {
      context.handle(
        _releaseIdMeta,
        releaseId.isAcceptableOrUnknown(data['release_id']!, _releaseIdMeta),
      );
    } else if (isInserting) {
      context.missing(_releaseIdMeta);
    }
    if (data.containsKey('title_id')) {
      context.handle(
        _titleIdMeta,
        titleId.isAcceptableOrUnknown(data['title_id']!, _titleIdMeta),
      );
    } else if (isInserting) {
      context.missing(_titleIdMeta);
    }
    if (data.containsKey('title_name')) {
      context.handle(
        _titleNameMeta,
        titleName.isAcceptableOrUnknown(data['title_name']!, _titleNameMeta),
      );
    }
    if (data.containsKey('platform_id')) {
      context.handle(
        _platformIdMeta,
        platformId.isAcceptableOrUnknown(data['platform_id']!, _platformIdMeta),
      );
    }
    if (data.containsKey('platform_name')) {
      context.handle(
        _platformNameMeta,
        platformName.isAcceptableOrUnknown(
          data['platform_name']!,
          _platformNameMeta,
        ),
      );
    }
    if (data.containsKey('platform_short_name')) {
      context.handle(
        _platformShortNameMeta,
        platformShortName.isAcceptableOrUnknown(
          data['platform_short_name']!,
          _platformShortNameMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_platformShortNameMeta);
    }
    if (data.containsKey('cover_url')) {
      context.handle(
        _coverUrlMeta,
        coverUrl.isAcceptableOrUnknown(data['cover_url']!, _coverUrlMeta),
      );
    }
    if (data.containsKey('release_name')) {
      context.handle(
        _releaseNameMeta,
        releaseName.isAcceptableOrUnknown(
          data['release_name']!,
          _releaseNameMeta,
        ),
      );
    }
    if (data.containsKey('release_revision')) {
      context.handle(
        _releaseRevisionMeta,
        releaseRevision.isAcceptableOrUnknown(
          data['release_revision']!,
          _releaseRevisionMeta,
        ),
      );
    }
    if (data.containsKey('content_root')) {
      context.handle(
        _contentRootMeta,
        contentRoot.isAcceptableOrUnknown(
          data['content_root']!,
          _contentRootMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_contentRootMeta);
    }
    if (data.containsKey('launch_relative_path')) {
      context.handle(
        _launchRelativePathMeta,
        launchRelativePath.isAcceptableOrUnknown(
          data['launch_relative_path']!,
          _launchRelativePathMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_launchRelativePathMeta);
    }
    if (data.containsKey('size_bytes')) {
      context.handle(
        _sizeBytesMeta,
        sizeBytes.isAcceptableOrUnknown(data['size_bytes']!, _sizeBytesMeta),
      );
    } else if (isInserting) {
      context.missing(_sizeBytesMeta);
    }
    if (data.containsKey('primary_sha256')) {
      context.handle(
        _primarySha256Meta,
        primarySha256.isAcceptableOrUnknown(
          data['primary_sha256']!,
          _primarySha256Meta,
        ),
      );
    }
    if (data.containsKey('manifest_fingerprint')) {
      context.handle(
        _manifestFingerprintMeta,
        manifestFingerprint.isAcceptableOrUnknown(
          data['manifest_fingerprint']!,
          _manifestFingerprintMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_manifestFingerprintMeta);
    }
    if (data.containsKey('state')) {
      context.handle(
        _stateMeta,
        state.isAcceptableOrUnknown(data['state']!, _stateMeta),
      );
    } else if (isInserting) {
      context.missing(_stateMeta);
    }
    if (data.containsKey('install_mode')) {
      context.handle(
        _installModeMeta,
        installMode.isAcceptableOrUnknown(
          data['install_mode']!,
          _installModeMeta,
        ),
      );
    }
    if (data.containsKey('manifest_snapshot')) {
      context.handle(
        _manifestSnapshotMeta,
        manifestSnapshot.isAcceptableOrUnknown(
          data['manifest_snapshot']!,
          _manifestSnapshotMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_manifestSnapshotMeta);
    }
    if (data.containsKey('installed_at')) {
      context.handle(
        _installedAtMeta,
        installedAt.isAcceptableOrUnknown(
          data['installed_at']!,
          _installedAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_installedAtMeta);
    }
    if (data.containsKey('last_played_at')) {
      context.handle(
        _lastPlayedAtMeta,
        lastPlayedAt.isAcceptableOrUnknown(
          data['last_played_at']!,
          _lastPlayedAtMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {releaseId};
  @override
  LegacyLocalInstallRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return LegacyLocalInstallRow(
      releaseId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_id'],
      )!,
      titleId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_id'],
      )!,
      titleName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_name'],
      )!,
      platformId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_id'],
      ),
      platformName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_name'],
      ),
      platformShortName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_short_name'],
      )!,
      coverUrl: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}cover_url'],
      ),
      releaseName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_name'],
      )!,
      releaseRevision: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_revision'],
      ),
      contentRoot: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}content_root'],
      )!,
      launchRelativePath: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}launch_relative_path'],
      )!,
      sizeBytes: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}size_bytes'],
      )!,
      primarySha256: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}primary_sha256'],
      ),
      manifestFingerprint: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}manifest_fingerprint'],
      )!,
      state: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}state'],
      )!,
      installMode: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}install_mode'],
      )!,
      manifestSnapshot: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}manifest_snapshot'],
      )!,
      installedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}installed_at'],
      )!,
      lastPlayedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_played_at'],
      ),
    );
  }

  @override
  $LegacyLocalInstallsTable createAlias(String alias) {
    return $LegacyLocalInstallsTable(attachedDatabase, alias);
  }
}

class LegacyLocalInstallRow extends DataClass
    implements Insertable<LegacyLocalInstallRow> {
  final String releaseId;
  final String titleId;
  final String titleName;
  final String? platformId;
  final String? platformName;
  final String platformShortName;
  final String? coverUrl;
  final String releaseName;
  final String? releaseRevision;
  final String contentRoot;
  final String launchRelativePath;
  final int sizeBytes;
  final String? primarySha256;
  final String manifestFingerprint;
  final String state;
  final String installMode;
  final String manifestSnapshot;
  final DateTime installedAt;
  final DateTime? lastPlayedAt;
  const LegacyLocalInstallRow({
    required this.releaseId,
    required this.titleId,
    required this.titleName,
    this.platformId,
    this.platformName,
    required this.platformShortName,
    this.coverUrl,
    required this.releaseName,
    this.releaseRevision,
    required this.contentRoot,
    required this.launchRelativePath,
    required this.sizeBytes,
    this.primarySha256,
    required this.manifestFingerprint,
    required this.state,
    required this.installMode,
    required this.manifestSnapshot,
    required this.installedAt,
    this.lastPlayedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['release_id'] = Variable<String>(releaseId);
    map['title_id'] = Variable<String>(titleId);
    map['title_name'] = Variable<String>(titleName);
    if (!nullToAbsent || platformId != null) {
      map['platform_id'] = Variable<String>(platformId);
    }
    if (!nullToAbsent || platformName != null) {
      map['platform_name'] = Variable<String>(platformName);
    }
    map['platform_short_name'] = Variable<String>(platformShortName);
    if (!nullToAbsent || coverUrl != null) {
      map['cover_url'] = Variable<String>(coverUrl);
    }
    map['release_name'] = Variable<String>(releaseName);
    if (!nullToAbsent || releaseRevision != null) {
      map['release_revision'] = Variable<String>(releaseRevision);
    }
    map['content_root'] = Variable<String>(contentRoot);
    map['launch_relative_path'] = Variable<String>(launchRelativePath);
    map['size_bytes'] = Variable<int>(sizeBytes);
    if (!nullToAbsent || primarySha256 != null) {
      map['primary_sha256'] = Variable<String>(primarySha256);
    }
    map['manifest_fingerprint'] = Variable<String>(manifestFingerprint);
    map['state'] = Variable<String>(state);
    map['install_mode'] = Variable<String>(installMode);
    map['manifest_snapshot'] = Variable<String>(manifestSnapshot);
    map['installed_at'] = Variable<DateTime>(installedAt);
    if (!nullToAbsent || lastPlayedAt != null) {
      map['last_played_at'] = Variable<DateTime>(lastPlayedAt);
    }
    return map;
  }

  LegacyLocalInstallsCompanion toCompanion(bool nullToAbsent) {
    return LegacyLocalInstallsCompanion(
      releaseId: Value(releaseId),
      titleId: Value(titleId),
      titleName: Value(titleName),
      platformId: platformId == null && nullToAbsent
          ? const Value.absent()
          : Value(platformId),
      platformName: platformName == null && nullToAbsent
          ? const Value.absent()
          : Value(platformName),
      platformShortName: Value(platformShortName),
      coverUrl: coverUrl == null && nullToAbsent
          ? const Value.absent()
          : Value(coverUrl),
      releaseName: Value(releaseName),
      releaseRevision: releaseRevision == null && nullToAbsent
          ? const Value.absent()
          : Value(releaseRevision),
      contentRoot: Value(contentRoot),
      launchRelativePath: Value(launchRelativePath),
      sizeBytes: Value(sizeBytes),
      primarySha256: primarySha256 == null && nullToAbsent
          ? const Value.absent()
          : Value(primarySha256),
      manifestFingerprint: Value(manifestFingerprint),
      state: Value(state),
      installMode: Value(installMode),
      manifestSnapshot: Value(manifestSnapshot),
      installedAt: Value(installedAt),
      lastPlayedAt: lastPlayedAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastPlayedAt),
    );
  }

  factory LegacyLocalInstallRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return LegacyLocalInstallRow(
      releaseId: serializer.fromJson<String>(json['releaseId']),
      titleId: serializer.fromJson<String>(json['titleId']),
      titleName: serializer.fromJson<String>(json['titleName']),
      platformId: serializer.fromJson<String?>(json['platformId']),
      platformName: serializer.fromJson<String?>(json['platformName']),
      platformShortName: serializer.fromJson<String>(json['platformShortName']),
      coverUrl: serializer.fromJson<String?>(json['coverUrl']),
      releaseName: serializer.fromJson<String>(json['releaseName']),
      releaseRevision: serializer.fromJson<String?>(json['releaseRevision']),
      contentRoot: serializer.fromJson<String>(json['contentRoot']),
      launchRelativePath: serializer.fromJson<String>(
        json['launchRelativePath'],
      ),
      sizeBytes: serializer.fromJson<int>(json['sizeBytes']),
      primarySha256: serializer.fromJson<String?>(json['primarySha256']),
      manifestFingerprint: serializer.fromJson<String>(
        json['manifestFingerprint'],
      ),
      state: serializer.fromJson<String>(json['state']),
      installMode: serializer.fromJson<String>(json['installMode']),
      manifestSnapshot: serializer.fromJson<String>(json['manifestSnapshot']),
      installedAt: serializer.fromJson<DateTime>(json['installedAt']),
      lastPlayedAt: serializer.fromJson<DateTime?>(json['lastPlayedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'releaseId': serializer.toJson<String>(releaseId),
      'titleId': serializer.toJson<String>(titleId),
      'titleName': serializer.toJson<String>(titleName),
      'platformId': serializer.toJson<String?>(platformId),
      'platformName': serializer.toJson<String?>(platformName),
      'platformShortName': serializer.toJson<String>(platformShortName),
      'coverUrl': serializer.toJson<String?>(coverUrl),
      'releaseName': serializer.toJson<String>(releaseName),
      'releaseRevision': serializer.toJson<String?>(releaseRevision),
      'contentRoot': serializer.toJson<String>(contentRoot),
      'launchRelativePath': serializer.toJson<String>(launchRelativePath),
      'sizeBytes': serializer.toJson<int>(sizeBytes),
      'primarySha256': serializer.toJson<String?>(primarySha256),
      'manifestFingerprint': serializer.toJson<String>(manifestFingerprint),
      'state': serializer.toJson<String>(state),
      'installMode': serializer.toJson<String>(installMode),
      'manifestSnapshot': serializer.toJson<String>(manifestSnapshot),
      'installedAt': serializer.toJson<DateTime>(installedAt),
      'lastPlayedAt': serializer.toJson<DateTime?>(lastPlayedAt),
    };
  }

  LegacyLocalInstallRow copyWith({
    String? releaseId,
    String? titleId,
    String? titleName,
    Value<String?> platformId = const Value.absent(),
    Value<String?> platformName = const Value.absent(),
    String? platformShortName,
    Value<String?> coverUrl = const Value.absent(),
    String? releaseName,
    Value<String?> releaseRevision = const Value.absent(),
    String? contentRoot,
    String? launchRelativePath,
    int? sizeBytes,
    Value<String?> primarySha256 = const Value.absent(),
    String? manifestFingerprint,
    String? state,
    String? installMode,
    String? manifestSnapshot,
    DateTime? installedAt,
    Value<DateTime?> lastPlayedAt = const Value.absent(),
  }) => LegacyLocalInstallRow(
    releaseId: releaseId ?? this.releaseId,
    titleId: titleId ?? this.titleId,
    titleName: titleName ?? this.titleName,
    platformId: platformId.present ? platformId.value : this.platformId,
    platformName: platformName.present ? platformName.value : this.platformName,
    platformShortName: platformShortName ?? this.platformShortName,
    coverUrl: coverUrl.present ? coverUrl.value : this.coverUrl,
    releaseName: releaseName ?? this.releaseName,
    releaseRevision: releaseRevision.present
        ? releaseRevision.value
        : this.releaseRevision,
    contentRoot: contentRoot ?? this.contentRoot,
    launchRelativePath: launchRelativePath ?? this.launchRelativePath,
    sizeBytes: sizeBytes ?? this.sizeBytes,
    primarySha256: primarySha256.present
        ? primarySha256.value
        : this.primarySha256,
    manifestFingerprint: manifestFingerprint ?? this.manifestFingerprint,
    state: state ?? this.state,
    installMode: installMode ?? this.installMode,
    manifestSnapshot: manifestSnapshot ?? this.manifestSnapshot,
    installedAt: installedAt ?? this.installedAt,
    lastPlayedAt: lastPlayedAt.present ? lastPlayedAt.value : this.lastPlayedAt,
  );
  LegacyLocalInstallRow copyWithCompanion(LegacyLocalInstallsCompanion data) {
    return LegacyLocalInstallRow(
      releaseId: data.releaseId.present ? data.releaseId.value : this.releaseId,
      titleId: data.titleId.present ? data.titleId.value : this.titleId,
      titleName: data.titleName.present ? data.titleName.value : this.titleName,
      platformId: data.platformId.present
          ? data.platformId.value
          : this.platformId,
      platformName: data.platformName.present
          ? data.platformName.value
          : this.platformName,
      platformShortName: data.platformShortName.present
          ? data.platformShortName.value
          : this.platformShortName,
      coverUrl: data.coverUrl.present ? data.coverUrl.value : this.coverUrl,
      releaseName: data.releaseName.present
          ? data.releaseName.value
          : this.releaseName,
      releaseRevision: data.releaseRevision.present
          ? data.releaseRevision.value
          : this.releaseRevision,
      contentRoot: data.contentRoot.present
          ? data.contentRoot.value
          : this.contentRoot,
      launchRelativePath: data.launchRelativePath.present
          ? data.launchRelativePath.value
          : this.launchRelativePath,
      sizeBytes: data.sizeBytes.present ? data.sizeBytes.value : this.sizeBytes,
      primarySha256: data.primarySha256.present
          ? data.primarySha256.value
          : this.primarySha256,
      manifestFingerprint: data.manifestFingerprint.present
          ? data.manifestFingerprint.value
          : this.manifestFingerprint,
      state: data.state.present ? data.state.value : this.state,
      installMode: data.installMode.present
          ? data.installMode.value
          : this.installMode,
      manifestSnapshot: data.manifestSnapshot.present
          ? data.manifestSnapshot.value
          : this.manifestSnapshot,
      installedAt: data.installedAt.present
          ? data.installedAt.value
          : this.installedAt,
      lastPlayedAt: data.lastPlayedAt.present
          ? data.lastPlayedAt.value
          : this.lastPlayedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('LegacyLocalInstallRow(')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('titleName: $titleName, ')
          ..write('platformId: $platformId, ')
          ..write('platformName: $platformName, ')
          ..write('platformShortName: $platformShortName, ')
          ..write('coverUrl: $coverUrl, ')
          ..write('releaseName: $releaseName, ')
          ..write('releaseRevision: $releaseRevision, ')
          ..write('contentRoot: $contentRoot, ')
          ..write('launchRelativePath: $launchRelativePath, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('primarySha256: $primarySha256, ')
          ..write('manifestFingerprint: $manifestFingerprint, ')
          ..write('state: $state, ')
          ..write('installMode: $installMode, ')
          ..write('manifestSnapshot: $manifestSnapshot, ')
          ..write('installedAt: $installedAt, ')
          ..write('lastPlayedAt: $lastPlayedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    releaseId,
    titleId,
    titleName,
    platformId,
    platformName,
    platformShortName,
    coverUrl,
    releaseName,
    releaseRevision,
    contentRoot,
    launchRelativePath,
    sizeBytes,
    primarySha256,
    manifestFingerprint,
    state,
    installMode,
    manifestSnapshot,
    installedAt,
    lastPlayedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is LegacyLocalInstallRow &&
          other.releaseId == this.releaseId &&
          other.titleId == this.titleId &&
          other.titleName == this.titleName &&
          other.platformId == this.platformId &&
          other.platformName == this.platformName &&
          other.platformShortName == this.platformShortName &&
          other.coverUrl == this.coverUrl &&
          other.releaseName == this.releaseName &&
          other.releaseRevision == this.releaseRevision &&
          other.contentRoot == this.contentRoot &&
          other.launchRelativePath == this.launchRelativePath &&
          other.sizeBytes == this.sizeBytes &&
          other.primarySha256 == this.primarySha256 &&
          other.manifestFingerprint == this.manifestFingerprint &&
          other.state == this.state &&
          other.installMode == this.installMode &&
          other.manifestSnapshot == this.manifestSnapshot &&
          other.installedAt == this.installedAt &&
          other.lastPlayedAt == this.lastPlayedAt);
}

class LegacyLocalInstallsCompanion
    extends UpdateCompanion<LegacyLocalInstallRow> {
  final Value<String> releaseId;
  final Value<String> titleId;
  final Value<String> titleName;
  final Value<String?> platformId;
  final Value<String?> platformName;
  final Value<String> platformShortName;
  final Value<String?> coverUrl;
  final Value<String> releaseName;
  final Value<String?> releaseRevision;
  final Value<String> contentRoot;
  final Value<String> launchRelativePath;
  final Value<int> sizeBytes;
  final Value<String?> primarySha256;
  final Value<String> manifestFingerprint;
  final Value<String> state;
  final Value<String> installMode;
  final Value<String> manifestSnapshot;
  final Value<DateTime> installedAt;
  final Value<DateTime?> lastPlayedAt;
  final Value<int> rowid;
  const LegacyLocalInstallsCompanion({
    this.releaseId = const Value.absent(),
    this.titleId = const Value.absent(),
    this.titleName = const Value.absent(),
    this.platformId = const Value.absent(),
    this.platformName = const Value.absent(),
    this.platformShortName = const Value.absent(),
    this.coverUrl = const Value.absent(),
    this.releaseName = const Value.absent(),
    this.releaseRevision = const Value.absent(),
    this.contentRoot = const Value.absent(),
    this.launchRelativePath = const Value.absent(),
    this.sizeBytes = const Value.absent(),
    this.primarySha256 = const Value.absent(),
    this.manifestFingerprint = const Value.absent(),
    this.state = const Value.absent(),
    this.installMode = const Value.absent(),
    this.manifestSnapshot = const Value.absent(),
    this.installedAt = const Value.absent(),
    this.lastPlayedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  LegacyLocalInstallsCompanion.insert({
    required String releaseId,
    required String titleId,
    this.titleName = const Value.absent(),
    this.platformId = const Value.absent(),
    this.platformName = const Value.absent(),
    required String platformShortName,
    this.coverUrl = const Value.absent(),
    this.releaseName = const Value.absent(),
    this.releaseRevision = const Value.absent(),
    required String contentRoot,
    required String launchRelativePath,
    required int sizeBytes,
    this.primarySha256 = const Value.absent(),
    required String manifestFingerprint,
    required String state,
    this.installMode = const Value.absent(),
    required String manifestSnapshot,
    required DateTime installedAt,
    this.lastPlayedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : releaseId = Value(releaseId),
       titleId = Value(titleId),
       platformShortName = Value(platformShortName),
       contentRoot = Value(contentRoot),
       launchRelativePath = Value(launchRelativePath),
       sizeBytes = Value(sizeBytes),
       manifestFingerprint = Value(manifestFingerprint),
       state = Value(state),
       manifestSnapshot = Value(manifestSnapshot),
       installedAt = Value(installedAt);
  static Insertable<LegacyLocalInstallRow> custom({
    Expression<String>? releaseId,
    Expression<String>? titleId,
    Expression<String>? titleName,
    Expression<String>? platformId,
    Expression<String>? platformName,
    Expression<String>? platformShortName,
    Expression<String>? coverUrl,
    Expression<String>? releaseName,
    Expression<String>? releaseRevision,
    Expression<String>? contentRoot,
    Expression<String>? launchRelativePath,
    Expression<int>? sizeBytes,
    Expression<String>? primarySha256,
    Expression<String>? manifestFingerprint,
    Expression<String>? state,
    Expression<String>? installMode,
    Expression<String>? manifestSnapshot,
    Expression<DateTime>? installedAt,
    Expression<DateTime>? lastPlayedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (releaseId != null) 'release_id': releaseId,
      if (titleId != null) 'title_id': titleId,
      if (titleName != null) 'title_name': titleName,
      if (platformId != null) 'platform_id': platformId,
      if (platformName != null) 'platform_name': platformName,
      if (platformShortName != null) 'platform_short_name': platformShortName,
      if (coverUrl != null) 'cover_url': coverUrl,
      if (releaseName != null) 'release_name': releaseName,
      if (releaseRevision != null) 'release_revision': releaseRevision,
      if (contentRoot != null) 'content_root': contentRoot,
      if (launchRelativePath != null)
        'launch_relative_path': launchRelativePath,
      if (sizeBytes != null) 'size_bytes': sizeBytes,
      if (primarySha256 != null) 'primary_sha256': primarySha256,
      if (manifestFingerprint != null)
        'manifest_fingerprint': manifestFingerprint,
      if (state != null) 'state': state,
      if (installMode != null) 'install_mode': installMode,
      if (manifestSnapshot != null) 'manifest_snapshot': manifestSnapshot,
      if (installedAt != null) 'installed_at': installedAt,
      if (lastPlayedAt != null) 'last_played_at': lastPlayedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  LegacyLocalInstallsCompanion copyWith({
    Value<String>? releaseId,
    Value<String>? titleId,
    Value<String>? titleName,
    Value<String?>? platformId,
    Value<String?>? platformName,
    Value<String>? platformShortName,
    Value<String?>? coverUrl,
    Value<String>? releaseName,
    Value<String?>? releaseRevision,
    Value<String>? contentRoot,
    Value<String>? launchRelativePath,
    Value<int>? sizeBytes,
    Value<String?>? primarySha256,
    Value<String>? manifestFingerprint,
    Value<String>? state,
    Value<String>? installMode,
    Value<String>? manifestSnapshot,
    Value<DateTime>? installedAt,
    Value<DateTime?>? lastPlayedAt,
    Value<int>? rowid,
  }) {
    return LegacyLocalInstallsCompanion(
      releaseId: releaseId ?? this.releaseId,
      titleId: titleId ?? this.titleId,
      titleName: titleName ?? this.titleName,
      platformId: platformId ?? this.platformId,
      platformName: platformName ?? this.platformName,
      platformShortName: platformShortName ?? this.platformShortName,
      coverUrl: coverUrl ?? this.coverUrl,
      releaseName: releaseName ?? this.releaseName,
      releaseRevision: releaseRevision ?? this.releaseRevision,
      contentRoot: contentRoot ?? this.contentRoot,
      launchRelativePath: launchRelativePath ?? this.launchRelativePath,
      sizeBytes: sizeBytes ?? this.sizeBytes,
      primarySha256: primarySha256 ?? this.primarySha256,
      manifestFingerprint: manifestFingerprint ?? this.manifestFingerprint,
      state: state ?? this.state,
      installMode: installMode ?? this.installMode,
      manifestSnapshot: manifestSnapshot ?? this.manifestSnapshot,
      installedAt: installedAt ?? this.installedAt,
      lastPlayedAt: lastPlayedAt ?? this.lastPlayedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (releaseId.present) {
      map['release_id'] = Variable<String>(releaseId.value);
    }
    if (titleId.present) {
      map['title_id'] = Variable<String>(titleId.value);
    }
    if (titleName.present) {
      map['title_name'] = Variable<String>(titleName.value);
    }
    if (platformId.present) {
      map['platform_id'] = Variable<String>(platformId.value);
    }
    if (platformName.present) {
      map['platform_name'] = Variable<String>(platformName.value);
    }
    if (platformShortName.present) {
      map['platform_short_name'] = Variable<String>(platformShortName.value);
    }
    if (coverUrl.present) {
      map['cover_url'] = Variable<String>(coverUrl.value);
    }
    if (releaseName.present) {
      map['release_name'] = Variable<String>(releaseName.value);
    }
    if (releaseRevision.present) {
      map['release_revision'] = Variable<String>(releaseRevision.value);
    }
    if (contentRoot.present) {
      map['content_root'] = Variable<String>(contentRoot.value);
    }
    if (launchRelativePath.present) {
      map['launch_relative_path'] = Variable<String>(launchRelativePath.value);
    }
    if (sizeBytes.present) {
      map['size_bytes'] = Variable<int>(sizeBytes.value);
    }
    if (primarySha256.present) {
      map['primary_sha256'] = Variable<String>(primarySha256.value);
    }
    if (manifestFingerprint.present) {
      map['manifest_fingerprint'] = Variable<String>(manifestFingerprint.value);
    }
    if (state.present) {
      map['state'] = Variable<String>(state.value);
    }
    if (installMode.present) {
      map['install_mode'] = Variable<String>(installMode.value);
    }
    if (manifestSnapshot.present) {
      map['manifest_snapshot'] = Variable<String>(manifestSnapshot.value);
    }
    if (installedAt.present) {
      map['installed_at'] = Variable<DateTime>(installedAt.value);
    }
    if (lastPlayedAt.present) {
      map['last_played_at'] = Variable<DateTime>(lastPlayedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('LegacyLocalInstallsCompanion(')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('titleName: $titleName, ')
          ..write('platformId: $platformId, ')
          ..write('platformName: $platformName, ')
          ..write('platformShortName: $platformShortName, ')
          ..write('coverUrl: $coverUrl, ')
          ..write('releaseName: $releaseName, ')
          ..write('releaseRevision: $releaseRevision, ')
          ..write('contentRoot: $contentRoot, ')
          ..write('launchRelativePath: $launchRelativePath, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('primarySha256: $primarySha256, ')
          ..write('manifestFingerprint: $manifestFingerprint, ')
          ..write('state: $state, ')
          ..write('installMode: $installMode, ')
          ..write('manifestSnapshot: $manifestSnapshot, ')
          ..write('installedAt: $installedAt, ')
          ..write('lastPlayedAt: $lastPlayedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $LocalInstallsTable extends LocalInstalls
    with TableInfo<$LocalInstallsTable, LocalInstallRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $LocalInstallsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _serverInstanceIdMeta = const VerificationMeta(
    'serverInstanceId',
  );
  @override
  late final GeneratedColumn<String> serverInstanceId = GeneratedColumn<String>(
    'server_instance_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES server_connections (instance_id)',
    ),
  );
  static const VerificationMeta _releaseIdMeta = const VerificationMeta(
    'releaseId',
  );
  @override
  late final GeneratedColumn<String> releaseId = GeneratedColumn<String>(
    'release_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleIdMeta = const VerificationMeta(
    'titleId',
  );
  @override
  late final GeneratedColumn<String> titleId = GeneratedColumn<String>(
    'title_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _titleNameMeta = const VerificationMeta(
    'titleName',
  );
  @override
  late final GeneratedColumn<String> titleName = GeneratedColumn<String>(
    'title_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _platformIdMeta = const VerificationMeta(
    'platformId',
  );
  @override
  late final GeneratedColumn<String> platformId = GeneratedColumn<String>(
    'platform_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _platformNameMeta = const VerificationMeta(
    'platformName',
  );
  @override
  late final GeneratedColumn<String> platformName = GeneratedColumn<String>(
    'platform_name',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _platformShortNameMeta = const VerificationMeta(
    'platformShortName',
  );
  @override
  late final GeneratedColumn<String> platformShortName =
      GeneratedColumn<String>(
        'platform_short_name',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _coverUrlMeta = const VerificationMeta(
    'coverUrl',
  );
  @override
  late final GeneratedColumn<String> coverUrl = GeneratedColumn<String>(
    'cover_url',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _releaseNameMeta = const VerificationMeta(
    'releaseName',
  );
  @override
  late final GeneratedColumn<String> releaseName = GeneratedColumn<String>(
    'release_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _releaseRevisionMeta = const VerificationMeta(
    'releaseRevision',
  );
  @override
  late final GeneratedColumn<String> releaseRevision = GeneratedColumn<String>(
    'release_revision',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _contentRootMeta = const VerificationMeta(
    'contentRoot',
  );
  @override
  late final GeneratedColumn<String> contentRoot = GeneratedColumn<String>(
    'content_root',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _launchRelativePathMeta =
      const VerificationMeta('launchRelativePath');
  @override
  late final GeneratedColumn<String> launchRelativePath =
      GeneratedColumn<String>(
        'launch_relative_path',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _sizeBytesMeta = const VerificationMeta(
    'sizeBytes',
  );
  @override
  late final GeneratedColumn<int> sizeBytes = GeneratedColumn<int>(
    'size_bytes',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _primarySha256Meta = const VerificationMeta(
    'primarySha256',
  );
  @override
  late final GeneratedColumn<String> primarySha256 = GeneratedColumn<String>(
    'primary_sha256',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _manifestFingerprintMeta =
      const VerificationMeta('manifestFingerprint');
  @override
  late final GeneratedColumn<String> manifestFingerprint =
      GeneratedColumn<String>(
        'manifest_fingerprint',
        aliasedName,
        false,
        type: DriftSqlType.string,
        requiredDuringInsert: true,
      );
  static const VerificationMeta _stateMeta = const VerificationMeta('state');
  @override
  late final GeneratedColumn<String> state = GeneratedColumn<String>(
    'state',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _installModeMeta = const VerificationMeta(
    'installMode',
  );
  @override
  late final GeneratedColumn<String> installMode = GeneratedColumn<String>(
    'install_mode',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant('permanent'),
  );
  static const VerificationMeta _manifestSnapshotMeta = const VerificationMeta(
    'manifestSnapshot',
  );
  @override
  late final GeneratedColumn<String> manifestSnapshot = GeneratedColumn<String>(
    'manifest_snapshot',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _installedAtMeta = const VerificationMeta(
    'installedAt',
  );
  @override
  late final GeneratedColumn<DateTime> installedAt = GeneratedColumn<DateTime>(
    'installed_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _lastPlayedAtMeta = const VerificationMeta(
    'lastPlayedAt',
  );
  @override
  late final GeneratedColumn<DateTime> lastPlayedAt = GeneratedColumn<DateTime>(
    'last_played_at',
    aliasedName,
    true,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: false,
  );
  @override
  List<GeneratedColumn> get $columns => [
    serverInstanceId,
    releaseId,
    titleId,
    titleName,
    platformId,
    platformName,
    platformShortName,
    coverUrl,
    releaseName,
    releaseRevision,
    contentRoot,
    launchRelativePath,
    sizeBytes,
    primarySha256,
    manifestFingerprint,
    state,
    installMode,
    manifestSnapshot,
    installedAt,
    lastPlayedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'local_installs';
  @override
  VerificationContext validateIntegrity(
    Insertable<LocalInstallRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('server_instance_id')) {
      context.handle(
        _serverInstanceIdMeta,
        serverInstanceId.isAcceptableOrUnknown(
          data['server_instance_id']!,
          _serverInstanceIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_serverInstanceIdMeta);
    }
    if (data.containsKey('release_id')) {
      context.handle(
        _releaseIdMeta,
        releaseId.isAcceptableOrUnknown(data['release_id']!, _releaseIdMeta),
      );
    } else if (isInserting) {
      context.missing(_releaseIdMeta);
    }
    if (data.containsKey('title_id')) {
      context.handle(
        _titleIdMeta,
        titleId.isAcceptableOrUnknown(data['title_id']!, _titleIdMeta),
      );
    } else if (isInserting) {
      context.missing(_titleIdMeta);
    }
    if (data.containsKey('title_name')) {
      context.handle(
        _titleNameMeta,
        titleName.isAcceptableOrUnknown(data['title_name']!, _titleNameMeta),
      );
    }
    if (data.containsKey('platform_id')) {
      context.handle(
        _platformIdMeta,
        platformId.isAcceptableOrUnknown(data['platform_id']!, _platformIdMeta),
      );
    } else if (isInserting) {
      context.missing(_platformIdMeta);
    }
    if (data.containsKey('platform_name')) {
      context.handle(
        _platformNameMeta,
        platformName.isAcceptableOrUnknown(
          data['platform_name']!,
          _platformNameMeta,
        ),
      );
    }
    if (data.containsKey('platform_short_name')) {
      context.handle(
        _platformShortNameMeta,
        platformShortName.isAcceptableOrUnknown(
          data['platform_short_name']!,
          _platformShortNameMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_platformShortNameMeta);
    }
    if (data.containsKey('cover_url')) {
      context.handle(
        _coverUrlMeta,
        coverUrl.isAcceptableOrUnknown(data['cover_url']!, _coverUrlMeta),
      );
    }
    if (data.containsKey('release_name')) {
      context.handle(
        _releaseNameMeta,
        releaseName.isAcceptableOrUnknown(
          data['release_name']!,
          _releaseNameMeta,
        ),
      );
    }
    if (data.containsKey('release_revision')) {
      context.handle(
        _releaseRevisionMeta,
        releaseRevision.isAcceptableOrUnknown(
          data['release_revision']!,
          _releaseRevisionMeta,
        ),
      );
    }
    if (data.containsKey('content_root')) {
      context.handle(
        _contentRootMeta,
        contentRoot.isAcceptableOrUnknown(
          data['content_root']!,
          _contentRootMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_contentRootMeta);
    }
    if (data.containsKey('launch_relative_path')) {
      context.handle(
        _launchRelativePathMeta,
        launchRelativePath.isAcceptableOrUnknown(
          data['launch_relative_path']!,
          _launchRelativePathMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_launchRelativePathMeta);
    }
    if (data.containsKey('size_bytes')) {
      context.handle(
        _sizeBytesMeta,
        sizeBytes.isAcceptableOrUnknown(data['size_bytes']!, _sizeBytesMeta),
      );
    } else if (isInserting) {
      context.missing(_sizeBytesMeta);
    }
    if (data.containsKey('primary_sha256')) {
      context.handle(
        _primarySha256Meta,
        primarySha256.isAcceptableOrUnknown(
          data['primary_sha256']!,
          _primarySha256Meta,
        ),
      );
    }
    if (data.containsKey('manifest_fingerprint')) {
      context.handle(
        _manifestFingerprintMeta,
        manifestFingerprint.isAcceptableOrUnknown(
          data['manifest_fingerprint']!,
          _manifestFingerprintMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_manifestFingerprintMeta);
    }
    if (data.containsKey('state')) {
      context.handle(
        _stateMeta,
        state.isAcceptableOrUnknown(data['state']!, _stateMeta),
      );
    } else if (isInserting) {
      context.missing(_stateMeta);
    }
    if (data.containsKey('install_mode')) {
      context.handle(
        _installModeMeta,
        installMode.isAcceptableOrUnknown(
          data['install_mode']!,
          _installModeMeta,
        ),
      );
    }
    if (data.containsKey('manifest_snapshot')) {
      context.handle(
        _manifestSnapshotMeta,
        manifestSnapshot.isAcceptableOrUnknown(
          data['manifest_snapshot']!,
          _manifestSnapshotMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_manifestSnapshotMeta);
    }
    if (data.containsKey('installed_at')) {
      context.handle(
        _installedAtMeta,
        installedAt.isAcceptableOrUnknown(
          data['installed_at']!,
          _installedAtMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_installedAtMeta);
    }
    if (data.containsKey('last_played_at')) {
      context.handle(
        _lastPlayedAtMeta,
        lastPlayedAt.isAcceptableOrUnknown(
          data['last_played_at']!,
          _lastPlayedAtMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {serverInstanceId, releaseId};
  @override
  LocalInstallRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return LocalInstallRow(
      serverInstanceId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_instance_id'],
      )!,
      releaseId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_id'],
      )!,
      titleId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_id'],
      )!,
      titleName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}title_name'],
      )!,
      platformId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_id'],
      )!,
      platformName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_name'],
      ),
      platformShortName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}platform_short_name'],
      )!,
      coverUrl: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}cover_url'],
      ),
      releaseName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_name'],
      )!,
      releaseRevision: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}release_revision'],
      ),
      contentRoot: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}content_root'],
      )!,
      launchRelativePath: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}launch_relative_path'],
      )!,
      sizeBytes: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}size_bytes'],
      )!,
      primarySha256: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}primary_sha256'],
      ),
      manifestFingerprint: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}manifest_fingerprint'],
      )!,
      state: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}state'],
      )!,
      installMode: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}install_mode'],
      )!,
      manifestSnapshot: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}manifest_snapshot'],
      )!,
      installedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}installed_at'],
      )!,
      lastPlayedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}last_played_at'],
      ),
    );
  }

  @override
  $LocalInstallsTable createAlias(String alias) {
    return $LocalInstallsTable(attachedDatabase, alias);
  }
}

class LocalInstallRow extends DataClass implements Insertable<LocalInstallRow> {
  final String serverInstanceId;
  final String releaseId;
  final String titleId;
  final String titleName;
  final String platformId;
  final String? platformName;
  final String platformShortName;
  final String? coverUrl;
  final String releaseName;
  final String? releaseRevision;
  final String contentRoot;
  final String launchRelativePath;
  final int sizeBytes;
  final String? primarySha256;
  final String manifestFingerprint;
  final String state;
  final String installMode;
  final String manifestSnapshot;
  final DateTime installedAt;
  final DateTime? lastPlayedAt;
  const LocalInstallRow({
    required this.serverInstanceId,
    required this.releaseId,
    required this.titleId,
    required this.titleName,
    required this.platformId,
    this.platformName,
    required this.platformShortName,
    this.coverUrl,
    required this.releaseName,
    this.releaseRevision,
    required this.contentRoot,
    required this.launchRelativePath,
    required this.sizeBytes,
    this.primarySha256,
    required this.manifestFingerprint,
    required this.state,
    required this.installMode,
    required this.manifestSnapshot,
    required this.installedAt,
    this.lastPlayedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['server_instance_id'] = Variable<String>(serverInstanceId);
    map['release_id'] = Variable<String>(releaseId);
    map['title_id'] = Variable<String>(titleId);
    map['title_name'] = Variable<String>(titleName);
    map['platform_id'] = Variable<String>(platformId);
    if (!nullToAbsent || platformName != null) {
      map['platform_name'] = Variable<String>(platformName);
    }
    map['platform_short_name'] = Variable<String>(platformShortName);
    if (!nullToAbsent || coverUrl != null) {
      map['cover_url'] = Variable<String>(coverUrl);
    }
    map['release_name'] = Variable<String>(releaseName);
    if (!nullToAbsent || releaseRevision != null) {
      map['release_revision'] = Variable<String>(releaseRevision);
    }
    map['content_root'] = Variable<String>(contentRoot);
    map['launch_relative_path'] = Variable<String>(launchRelativePath);
    map['size_bytes'] = Variable<int>(sizeBytes);
    if (!nullToAbsent || primarySha256 != null) {
      map['primary_sha256'] = Variable<String>(primarySha256);
    }
    map['manifest_fingerprint'] = Variable<String>(manifestFingerprint);
    map['state'] = Variable<String>(state);
    map['install_mode'] = Variable<String>(installMode);
    map['manifest_snapshot'] = Variable<String>(manifestSnapshot);
    map['installed_at'] = Variable<DateTime>(installedAt);
    if (!nullToAbsent || lastPlayedAt != null) {
      map['last_played_at'] = Variable<DateTime>(lastPlayedAt);
    }
    return map;
  }

  LocalInstallsCompanion toCompanion(bool nullToAbsent) {
    return LocalInstallsCompanion(
      serverInstanceId: Value(serverInstanceId),
      releaseId: Value(releaseId),
      titleId: Value(titleId),
      titleName: Value(titleName),
      platformId: Value(platformId),
      platformName: platformName == null && nullToAbsent
          ? const Value.absent()
          : Value(platformName),
      platformShortName: Value(platformShortName),
      coverUrl: coverUrl == null && nullToAbsent
          ? const Value.absent()
          : Value(coverUrl),
      releaseName: Value(releaseName),
      releaseRevision: releaseRevision == null && nullToAbsent
          ? const Value.absent()
          : Value(releaseRevision),
      contentRoot: Value(contentRoot),
      launchRelativePath: Value(launchRelativePath),
      sizeBytes: Value(sizeBytes),
      primarySha256: primarySha256 == null && nullToAbsent
          ? const Value.absent()
          : Value(primarySha256),
      manifestFingerprint: Value(manifestFingerprint),
      state: Value(state),
      installMode: Value(installMode),
      manifestSnapshot: Value(manifestSnapshot),
      installedAt: Value(installedAt),
      lastPlayedAt: lastPlayedAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastPlayedAt),
    );
  }

  factory LocalInstallRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return LocalInstallRow(
      serverInstanceId: serializer.fromJson<String>(json['serverInstanceId']),
      releaseId: serializer.fromJson<String>(json['releaseId']),
      titleId: serializer.fromJson<String>(json['titleId']),
      titleName: serializer.fromJson<String>(json['titleName']),
      platformId: serializer.fromJson<String>(json['platformId']),
      platformName: serializer.fromJson<String?>(json['platformName']),
      platformShortName: serializer.fromJson<String>(json['platformShortName']),
      coverUrl: serializer.fromJson<String?>(json['coverUrl']),
      releaseName: serializer.fromJson<String>(json['releaseName']),
      releaseRevision: serializer.fromJson<String?>(json['releaseRevision']),
      contentRoot: serializer.fromJson<String>(json['contentRoot']),
      launchRelativePath: serializer.fromJson<String>(
        json['launchRelativePath'],
      ),
      sizeBytes: serializer.fromJson<int>(json['sizeBytes']),
      primarySha256: serializer.fromJson<String?>(json['primarySha256']),
      manifestFingerprint: serializer.fromJson<String>(
        json['manifestFingerprint'],
      ),
      state: serializer.fromJson<String>(json['state']),
      installMode: serializer.fromJson<String>(json['installMode']),
      manifestSnapshot: serializer.fromJson<String>(json['manifestSnapshot']),
      installedAt: serializer.fromJson<DateTime>(json['installedAt']),
      lastPlayedAt: serializer.fromJson<DateTime?>(json['lastPlayedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'serverInstanceId': serializer.toJson<String>(serverInstanceId),
      'releaseId': serializer.toJson<String>(releaseId),
      'titleId': serializer.toJson<String>(titleId),
      'titleName': serializer.toJson<String>(titleName),
      'platformId': serializer.toJson<String>(platformId),
      'platformName': serializer.toJson<String?>(platformName),
      'platformShortName': serializer.toJson<String>(platformShortName),
      'coverUrl': serializer.toJson<String?>(coverUrl),
      'releaseName': serializer.toJson<String>(releaseName),
      'releaseRevision': serializer.toJson<String?>(releaseRevision),
      'contentRoot': serializer.toJson<String>(contentRoot),
      'launchRelativePath': serializer.toJson<String>(launchRelativePath),
      'sizeBytes': serializer.toJson<int>(sizeBytes),
      'primarySha256': serializer.toJson<String?>(primarySha256),
      'manifestFingerprint': serializer.toJson<String>(manifestFingerprint),
      'state': serializer.toJson<String>(state),
      'installMode': serializer.toJson<String>(installMode),
      'manifestSnapshot': serializer.toJson<String>(manifestSnapshot),
      'installedAt': serializer.toJson<DateTime>(installedAt),
      'lastPlayedAt': serializer.toJson<DateTime?>(lastPlayedAt),
    };
  }

  LocalInstallRow copyWith({
    String? serverInstanceId,
    String? releaseId,
    String? titleId,
    String? titleName,
    String? platformId,
    Value<String?> platformName = const Value.absent(),
    String? platformShortName,
    Value<String?> coverUrl = const Value.absent(),
    String? releaseName,
    Value<String?> releaseRevision = const Value.absent(),
    String? contentRoot,
    String? launchRelativePath,
    int? sizeBytes,
    Value<String?> primarySha256 = const Value.absent(),
    String? manifestFingerprint,
    String? state,
    String? installMode,
    String? manifestSnapshot,
    DateTime? installedAt,
    Value<DateTime?> lastPlayedAt = const Value.absent(),
  }) => LocalInstallRow(
    serverInstanceId: serverInstanceId ?? this.serverInstanceId,
    releaseId: releaseId ?? this.releaseId,
    titleId: titleId ?? this.titleId,
    titleName: titleName ?? this.titleName,
    platformId: platformId ?? this.platformId,
    platformName: platformName.present ? platformName.value : this.platformName,
    platformShortName: platformShortName ?? this.platformShortName,
    coverUrl: coverUrl.present ? coverUrl.value : this.coverUrl,
    releaseName: releaseName ?? this.releaseName,
    releaseRevision: releaseRevision.present
        ? releaseRevision.value
        : this.releaseRevision,
    contentRoot: contentRoot ?? this.contentRoot,
    launchRelativePath: launchRelativePath ?? this.launchRelativePath,
    sizeBytes: sizeBytes ?? this.sizeBytes,
    primarySha256: primarySha256.present
        ? primarySha256.value
        : this.primarySha256,
    manifestFingerprint: manifestFingerprint ?? this.manifestFingerprint,
    state: state ?? this.state,
    installMode: installMode ?? this.installMode,
    manifestSnapshot: manifestSnapshot ?? this.manifestSnapshot,
    installedAt: installedAt ?? this.installedAt,
    lastPlayedAt: lastPlayedAt.present ? lastPlayedAt.value : this.lastPlayedAt,
  );
  LocalInstallRow copyWithCompanion(LocalInstallsCompanion data) {
    return LocalInstallRow(
      serverInstanceId: data.serverInstanceId.present
          ? data.serverInstanceId.value
          : this.serverInstanceId,
      releaseId: data.releaseId.present ? data.releaseId.value : this.releaseId,
      titleId: data.titleId.present ? data.titleId.value : this.titleId,
      titleName: data.titleName.present ? data.titleName.value : this.titleName,
      platformId: data.platformId.present
          ? data.platformId.value
          : this.platformId,
      platformName: data.platformName.present
          ? data.platformName.value
          : this.platformName,
      platformShortName: data.platformShortName.present
          ? data.platformShortName.value
          : this.platformShortName,
      coverUrl: data.coverUrl.present ? data.coverUrl.value : this.coverUrl,
      releaseName: data.releaseName.present
          ? data.releaseName.value
          : this.releaseName,
      releaseRevision: data.releaseRevision.present
          ? data.releaseRevision.value
          : this.releaseRevision,
      contentRoot: data.contentRoot.present
          ? data.contentRoot.value
          : this.contentRoot,
      launchRelativePath: data.launchRelativePath.present
          ? data.launchRelativePath.value
          : this.launchRelativePath,
      sizeBytes: data.sizeBytes.present ? data.sizeBytes.value : this.sizeBytes,
      primarySha256: data.primarySha256.present
          ? data.primarySha256.value
          : this.primarySha256,
      manifestFingerprint: data.manifestFingerprint.present
          ? data.manifestFingerprint.value
          : this.manifestFingerprint,
      state: data.state.present ? data.state.value : this.state,
      installMode: data.installMode.present
          ? data.installMode.value
          : this.installMode,
      manifestSnapshot: data.manifestSnapshot.present
          ? data.manifestSnapshot.value
          : this.manifestSnapshot,
      installedAt: data.installedAt.present
          ? data.installedAt.value
          : this.installedAt,
      lastPlayedAt: data.lastPlayedAt.present
          ? data.lastPlayedAt.value
          : this.lastPlayedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('LocalInstallRow(')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('titleName: $titleName, ')
          ..write('platformId: $platformId, ')
          ..write('platformName: $platformName, ')
          ..write('platformShortName: $platformShortName, ')
          ..write('coverUrl: $coverUrl, ')
          ..write('releaseName: $releaseName, ')
          ..write('releaseRevision: $releaseRevision, ')
          ..write('contentRoot: $contentRoot, ')
          ..write('launchRelativePath: $launchRelativePath, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('primarySha256: $primarySha256, ')
          ..write('manifestFingerprint: $manifestFingerprint, ')
          ..write('state: $state, ')
          ..write('installMode: $installMode, ')
          ..write('manifestSnapshot: $manifestSnapshot, ')
          ..write('installedAt: $installedAt, ')
          ..write('lastPlayedAt: $lastPlayedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    serverInstanceId,
    releaseId,
    titleId,
    titleName,
    platformId,
    platformName,
    platformShortName,
    coverUrl,
    releaseName,
    releaseRevision,
    contentRoot,
    launchRelativePath,
    sizeBytes,
    primarySha256,
    manifestFingerprint,
    state,
    installMode,
    manifestSnapshot,
    installedAt,
    lastPlayedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is LocalInstallRow &&
          other.serverInstanceId == this.serverInstanceId &&
          other.releaseId == this.releaseId &&
          other.titleId == this.titleId &&
          other.titleName == this.titleName &&
          other.platformId == this.platformId &&
          other.platformName == this.platformName &&
          other.platformShortName == this.platformShortName &&
          other.coverUrl == this.coverUrl &&
          other.releaseName == this.releaseName &&
          other.releaseRevision == this.releaseRevision &&
          other.contentRoot == this.contentRoot &&
          other.launchRelativePath == this.launchRelativePath &&
          other.sizeBytes == this.sizeBytes &&
          other.primarySha256 == this.primarySha256 &&
          other.manifestFingerprint == this.manifestFingerprint &&
          other.state == this.state &&
          other.installMode == this.installMode &&
          other.manifestSnapshot == this.manifestSnapshot &&
          other.installedAt == this.installedAt &&
          other.lastPlayedAt == this.lastPlayedAt);
}

class LocalInstallsCompanion extends UpdateCompanion<LocalInstallRow> {
  final Value<String> serverInstanceId;
  final Value<String> releaseId;
  final Value<String> titleId;
  final Value<String> titleName;
  final Value<String> platformId;
  final Value<String?> platformName;
  final Value<String> platformShortName;
  final Value<String?> coverUrl;
  final Value<String> releaseName;
  final Value<String?> releaseRevision;
  final Value<String> contentRoot;
  final Value<String> launchRelativePath;
  final Value<int> sizeBytes;
  final Value<String?> primarySha256;
  final Value<String> manifestFingerprint;
  final Value<String> state;
  final Value<String> installMode;
  final Value<String> manifestSnapshot;
  final Value<DateTime> installedAt;
  final Value<DateTime?> lastPlayedAt;
  final Value<int> rowid;
  const LocalInstallsCompanion({
    this.serverInstanceId = const Value.absent(),
    this.releaseId = const Value.absent(),
    this.titleId = const Value.absent(),
    this.titleName = const Value.absent(),
    this.platformId = const Value.absent(),
    this.platformName = const Value.absent(),
    this.platformShortName = const Value.absent(),
    this.coverUrl = const Value.absent(),
    this.releaseName = const Value.absent(),
    this.releaseRevision = const Value.absent(),
    this.contentRoot = const Value.absent(),
    this.launchRelativePath = const Value.absent(),
    this.sizeBytes = const Value.absent(),
    this.primarySha256 = const Value.absent(),
    this.manifestFingerprint = const Value.absent(),
    this.state = const Value.absent(),
    this.installMode = const Value.absent(),
    this.manifestSnapshot = const Value.absent(),
    this.installedAt = const Value.absent(),
    this.lastPlayedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  LocalInstallsCompanion.insert({
    required String serverInstanceId,
    required String releaseId,
    required String titleId,
    this.titleName = const Value.absent(),
    required String platformId,
    this.platformName = const Value.absent(),
    required String platformShortName,
    this.coverUrl = const Value.absent(),
    this.releaseName = const Value.absent(),
    this.releaseRevision = const Value.absent(),
    required String contentRoot,
    required String launchRelativePath,
    required int sizeBytes,
    this.primarySha256 = const Value.absent(),
    required String manifestFingerprint,
    required String state,
    this.installMode = const Value.absent(),
    required String manifestSnapshot,
    required DateTime installedAt,
    this.lastPlayedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : serverInstanceId = Value(serverInstanceId),
       releaseId = Value(releaseId),
       titleId = Value(titleId),
       platformId = Value(platformId),
       platformShortName = Value(platformShortName),
       contentRoot = Value(contentRoot),
       launchRelativePath = Value(launchRelativePath),
       sizeBytes = Value(sizeBytes),
       manifestFingerprint = Value(manifestFingerprint),
       state = Value(state),
       manifestSnapshot = Value(manifestSnapshot),
       installedAt = Value(installedAt);
  static Insertable<LocalInstallRow> custom({
    Expression<String>? serverInstanceId,
    Expression<String>? releaseId,
    Expression<String>? titleId,
    Expression<String>? titleName,
    Expression<String>? platformId,
    Expression<String>? platformName,
    Expression<String>? platformShortName,
    Expression<String>? coverUrl,
    Expression<String>? releaseName,
    Expression<String>? releaseRevision,
    Expression<String>? contentRoot,
    Expression<String>? launchRelativePath,
    Expression<int>? sizeBytes,
    Expression<String>? primarySha256,
    Expression<String>? manifestFingerprint,
    Expression<String>? state,
    Expression<String>? installMode,
    Expression<String>? manifestSnapshot,
    Expression<DateTime>? installedAt,
    Expression<DateTime>? lastPlayedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (serverInstanceId != null) 'server_instance_id': serverInstanceId,
      if (releaseId != null) 'release_id': releaseId,
      if (titleId != null) 'title_id': titleId,
      if (titleName != null) 'title_name': titleName,
      if (platformId != null) 'platform_id': platformId,
      if (platformName != null) 'platform_name': platformName,
      if (platformShortName != null) 'platform_short_name': platformShortName,
      if (coverUrl != null) 'cover_url': coverUrl,
      if (releaseName != null) 'release_name': releaseName,
      if (releaseRevision != null) 'release_revision': releaseRevision,
      if (contentRoot != null) 'content_root': contentRoot,
      if (launchRelativePath != null)
        'launch_relative_path': launchRelativePath,
      if (sizeBytes != null) 'size_bytes': sizeBytes,
      if (primarySha256 != null) 'primary_sha256': primarySha256,
      if (manifestFingerprint != null)
        'manifest_fingerprint': manifestFingerprint,
      if (state != null) 'state': state,
      if (installMode != null) 'install_mode': installMode,
      if (manifestSnapshot != null) 'manifest_snapshot': manifestSnapshot,
      if (installedAt != null) 'installed_at': installedAt,
      if (lastPlayedAt != null) 'last_played_at': lastPlayedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  LocalInstallsCompanion copyWith({
    Value<String>? serverInstanceId,
    Value<String>? releaseId,
    Value<String>? titleId,
    Value<String>? titleName,
    Value<String>? platformId,
    Value<String?>? platformName,
    Value<String>? platformShortName,
    Value<String?>? coverUrl,
    Value<String>? releaseName,
    Value<String?>? releaseRevision,
    Value<String>? contentRoot,
    Value<String>? launchRelativePath,
    Value<int>? sizeBytes,
    Value<String?>? primarySha256,
    Value<String>? manifestFingerprint,
    Value<String>? state,
    Value<String>? installMode,
    Value<String>? manifestSnapshot,
    Value<DateTime>? installedAt,
    Value<DateTime?>? lastPlayedAt,
    Value<int>? rowid,
  }) {
    return LocalInstallsCompanion(
      serverInstanceId: serverInstanceId ?? this.serverInstanceId,
      releaseId: releaseId ?? this.releaseId,
      titleId: titleId ?? this.titleId,
      titleName: titleName ?? this.titleName,
      platformId: platformId ?? this.platformId,
      platformName: platformName ?? this.platformName,
      platformShortName: platformShortName ?? this.platformShortName,
      coverUrl: coverUrl ?? this.coverUrl,
      releaseName: releaseName ?? this.releaseName,
      releaseRevision: releaseRevision ?? this.releaseRevision,
      contentRoot: contentRoot ?? this.contentRoot,
      launchRelativePath: launchRelativePath ?? this.launchRelativePath,
      sizeBytes: sizeBytes ?? this.sizeBytes,
      primarySha256: primarySha256 ?? this.primarySha256,
      manifestFingerprint: manifestFingerprint ?? this.manifestFingerprint,
      state: state ?? this.state,
      installMode: installMode ?? this.installMode,
      manifestSnapshot: manifestSnapshot ?? this.manifestSnapshot,
      installedAt: installedAt ?? this.installedAt,
      lastPlayedAt: lastPlayedAt ?? this.lastPlayedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (serverInstanceId.present) {
      map['server_instance_id'] = Variable<String>(serverInstanceId.value);
    }
    if (releaseId.present) {
      map['release_id'] = Variable<String>(releaseId.value);
    }
    if (titleId.present) {
      map['title_id'] = Variable<String>(titleId.value);
    }
    if (titleName.present) {
      map['title_name'] = Variable<String>(titleName.value);
    }
    if (platformId.present) {
      map['platform_id'] = Variable<String>(platformId.value);
    }
    if (platformName.present) {
      map['platform_name'] = Variable<String>(platformName.value);
    }
    if (platformShortName.present) {
      map['platform_short_name'] = Variable<String>(platformShortName.value);
    }
    if (coverUrl.present) {
      map['cover_url'] = Variable<String>(coverUrl.value);
    }
    if (releaseName.present) {
      map['release_name'] = Variable<String>(releaseName.value);
    }
    if (releaseRevision.present) {
      map['release_revision'] = Variable<String>(releaseRevision.value);
    }
    if (contentRoot.present) {
      map['content_root'] = Variable<String>(contentRoot.value);
    }
    if (launchRelativePath.present) {
      map['launch_relative_path'] = Variable<String>(launchRelativePath.value);
    }
    if (sizeBytes.present) {
      map['size_bytes'] = Variable<int>(sizeBytes.value);
    }
    if (primarySha256.present) {
      map['primary_sha256'] = Variable<String>(primarySha256.value);
    }
    if (manifestFingerprint.present) {
      map['manifest_fingerprint'] = Variable<String>(manifestFingerprint.value);
    }
    if (state.present) {
      map['state'] = Variable<String>(state.value);
    }
    if (installMode.present) {
      map['install_mode'] = Variable<String>(installMode.value);
    }
    if (manifestSnapshot.present) {
      map['manifest_snapshot'] = Variable<String>(manifestSnapshot.value);
    }
    if (installedAt.present) {
      map['installed_at'] = Variable<DateTime>(installedAt.value);
    }
    if (lastPlayedAt.present) {
      map['last_played_at'] = Variable<DateTime>(lastPlayedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('LocalInstallsCompanion(')
          ..write('serverInstanceId: $serverInstanceId, ')
          ..write('releaseId: $releaseId, ')
          ..write('titleId: $titleId, ')
          ..write('titleName: $titleName, ')
          ..write('platformId: $platformId, ')
          ..write('platformName: $platformName, ')
          ..write('platformShortName: $platformShortName, ')
          ..write('coverUrl: $coverUrl, ')
          ..write('releaseName: $releaseName, ')
          ..write('releaseRevision: $releaseRevision, ')
          ..write('contentRoot: $contentRoot, ')
          ..write('launchRelativePath: $launchRelativePath, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('primarySha256: $primarySha256, ')
          ..write('manifestFingerprint: $manifestFingerprint, ')
          ..write('state: $state, ')
          ..write('installMode: $installMode, ')
          ..write('manifestSnapshot: $manifestSnapshot, ')
          ..write('installedAt: $installedAt, ')
          ..write('lastPlayedAt: $lastPlayedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $RuntimeOverrideRulesTable extends RuntimeOverrideRules
    with TableInfo<$RuntimeOverrideRulesTable, RuntimeOverrideRuleRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $RuntimeOverrideRulesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _scopeMeta = const VerificationMeta('scope');
  @override
  late final GeneratedColumn<String> scope = GeneratedColumn<String>(
    'scope',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _scopeValueMeta = const VerificationMeta(
    'scopeValue',
  );
  @override
  late final GeneratedColumn<String> scopeValue = GeneratedColumn<String>(
    'scope_value',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _profileIdMeta = const VerificationMeta(
    'profileId',
  );
  @override
  late final GeneratedColumn<String> profileId = GeneratedColumn<String>(
    'profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    scope,
    scopeValue,
    profileId,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'runtime_override_rules';
  @override
  VerificationContext validateIntegrity(
    Insertable<RuntimeOverrideRuleRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('scope')) {
      context.handle(
        _scopeMeta,
        scope.isAcceptableOrUnknown(data['scope']!, _scopeMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeMeta);
    }
    if (data.containsKey('scope_value')) {
      context.handle(
        _scopeValueMeta,
        scopeValue.isAcceptableOrUnknown(data['scope_value']!, _scopeValueMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeValueMeta);
    }
    if (data.containsKey('profile_id')) {
      context.handle(
        _profileIdMeta,
        profileId.isAcceptableOrUnknown(data['profile_id']!, _profileIdMeta),
      );
    } else if (isInserting) {
      context.missing(_profileIdMeta);
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {scope, scopeValue};
  @override
  RuntimeOverrideRuleRow map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return RuntimeOverrideRuleRow(
      scope: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope'],
      )!,
      scopeValue: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope_value'],
      )!,
      profileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}profile_id'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $RuntimeOverrideRulesTable createAlias(String alias) {
    return $RuntimeOverrideRulesTable(attachedDatabase, alias);
  }
}

class RuntimeOverrideRuleRow extends DataClass
    implements Insertable<RuntimeOverrideRuleRow> {
  final String scope;
  final String scopeValue;
  final String profileId;
  final DateTime updatedAt;
  const RuntimeOverrideRuleRow({
    required this.scope,
    required this.scopeValue,
    required this.profileId,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['scope'] = Variable<String>(scope);
    map['scope_value'] = Variable<String>(scopeValue);
    map['profile_id'] = Variable<String>(profileId);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  RuntimeOverrideRulesCompanion toCompanion(bool nullToAbsent) {
    return RuntimeOverrideRulesCompanion(
      scope: Value(scope),
      scopeValue: Value(scopeValue),
      profileId: Value(profileId),
      updatedAt: Value(updatedAt),
    );
  }

  factory RuntimeOverrideRuleRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return RuntimeOverrideRuleRow(
      scope: serializer.fromJson<String>(json['scope']),
      scopeValue: serializer.fromJson<String>(json['scopeValue']),
      profileId: serializer.fromJson<String>(json['profileId']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'scope': serializer.toJson<String>(scope),
      'scopeValue': serializer.toJson<String>(scopeValue),
      'profileId': serializer.toJson<String>(profileId),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  RuntimeOverrideRuleRow copyWith({
    String? scope,
    String? scopeValue,
    String? profileId,
    DateTime? updatedAt,
  }) => RuntimeOverrideRuleRow(
    scope: scope ?? this.scope,
    scopeValue: scopeValue ?? this.scopeValue,
    profileId: profileId ?? this.profileId,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  RuntimeOverrideRuleRow copyWithCompanion(RuntimeOverrideRulesCompanion data) {
    return RuntimeOverrideRuleRow(
      scope: data.scope.present ? data.scope.value : this.scope,
      scopeValue: data.scopeValue.present
          ? data.scopeValue.value
          : this.scopeValue,
      profileId: data.profileId.present ? data.profileId.value : this.profileId,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('RuntimeOverrideRuleRow(')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('profileId: $profileId, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(scope, scopeValue, profileId, updatedAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is RuntimeOverrideRuleRow &&
          other.scope == this.scope &&
          other.scopeValue == this.scopeValue &&
          other.profileId == this.profileId &&
          other.updatedAt == this.updatedAt);
}

class RuntimeOverrideRulesCompanion
    extends UpdateCompanion<RuntimeOverrideRuleRow> {
  final Value<String> scope;
  final Value<String> scopeValue;
  final Value<String> profileId;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const RuntimeOverrideRulesCompanion({
    this.scope = const Value.absent(),
    this.scopeValue = const Value.absent(),
    this.profileId = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  RuntimeOverrideRulesCompanion.insert({
    required String scope,
    required String scopeValue,
    required String profileId,
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : scope = Value(scope),
       scopeValue = Value(scopeValue),
       profileId = Value(profileId),
       updatedAt = Value(updatedAt);
  static Insertable<RuntimeOverrideRuleRow> custom({
    Expression<String>? scope,
    Expression<String>? scopeValue,
    Expression<String>? profileId,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (scope != null) 'scope': scope,
      if (scopeValue != null) 'scope_value': scopeValue,
      if (profileId != null) 'profile_id': profileId,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  RuntimeOverrideRulesCompanion copyWith({
    Value<String>? scope,
    Value<String>? scopeValue,
    Value<String>? profileId,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return RuntimeOverrideRulesCompanion(
      scope: scope ?? this.scope,
      scopeValue: scopeValue ?? this.scopeValue,
      profileId: profileId ?? this.profileId,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (scope.present) {
      map['scope'] = Variable<String>(scope.value);
    }
    if (scopeValue.present) {
      map['scope_value'] = Variable<String>(scopeValue.value);
    }
    if (profileId.present) {
      map['profile_id'] = Variable<String>(profileId.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('RuntimeOverrideRulesCompanion(')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('profileId: $profileId, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ControllerBindingRulesTable extends ControllerBindingRules
    with TableInfo<$ControllerBindingRulesTable, ControllerBindingRuleRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ControllerBindingRulesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _scopeMeta = const VerificationMeta('scope');
  @override
  late final GeneratedColumn<String> scope = GeneratedColumn<String>(
    'scope',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _scopeValueMeta = const VerificationMeta(
    'scopeValue',
  );
  @override
  late final GeneratedColumn<String> scopeValue = GeneratedColumn<String>(
    'scope_value',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _actionMeta = const VerificationMeta('action');
  @override
  late final GeneratedColumn<String> action = GeneratedColumn<String>(
    'action',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _buttonMeta = const VerificationMeta('button');
  @override
  late final GeneratedColumn<String> button = GeneratedColumn<String>(
    'button',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    scope,
    scopeValue,
    action,
    button,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'controller_binding_rules';
  @override
  VerificationContext validateIntegrity(
    Insertable<ControllerBindingRuleRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('scope')) {
      context.handle(
        _scopeMeta,
        scope.isAcceptableOrUnknown(data['scope']!, _scopeMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeMeta);
    }
    if (data.containsKey('scope_value')) {
      context.handle(
        _scopeValueMeta,
        scopeValue.isAcceptableOrUnknown(data['scope_value']!, _scopeValueMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeValueMeta);
    }
    if (data.containsKey('action')) {
      context.handle(
        _actionMeta,
        action.isAcceptableOrUnknown(data['action']!, _actionMeta),
      );
    } else if (isInserting) {
      context.missing(_actionMeta);
    }
    if (data.containsKey('button')) {
      context.handle(
        _buttonMeta,
        button.isAcceptableOrUnknown(data['button']!, _buttonMeta),
      );
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {scope, scopeValue, action};
  @override
  ControllerBindingRuleRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ControllerBindingRuleRow(
      scope: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope'],
      )!,
      scopeValue: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope_value'],
      )!,
      action: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}action'],
      )!,
      button: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}button'],
      ),
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $ControllerBindingRulesTable createAlias(String alias) {
    return $ControllerBindingRulesTable(attachedDatabase, alias);
  }
}

class ControllerBindingRuleRow extends DataClass
    implements Insertable<ControllerBindingRuleRow> {
  final String scope;
  final String scopeValue;
  final String action;
  final String? button;
  final DateTime updatedAt;
  const ControllerBindingRuleRow({
    required this.scope,
    required this.scopeValue,
    required this.action,
    this.button,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['scope'] = Variable<String>(scope);
    map['scope_value'] = Variable<String>(scopeValue);
    map['action'] = Variable<String>(action);
    if (!nullToAbsent || button != null) {
      map['button'] = Variable<String>(button);
    }
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  ControllerBindingRulesCompanion toCompanion(bool nullToAbsent) {
    return ControllerBindingRulesCompanion(
      scope: Value(scope),
      scopeValue: Value(scopeValue),
      action: Value(action),
      button: button == null && nullToAbsent
          ? const Value.absent()
          : Value(button),
      updatedAt: Value(updatedAt),
    );
  }

  factory ControllerBindingRuleRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ControllerBindingRuleRow(
      scope: serializer.fromJson<String>(json['scope']),
      scopeValue: serializer.fromJson<String>(json['scopeValue']),
      action: serializer.fromJson<String>(json['action']),
      button: serializer.fromJson<String?>(json['button']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'scope': serializer.toJson<String>(scope),
      'scopeValue': serializer.toJson<String>(scopeValue),
      'action': serializer.toJson<String>(action),
      'button': serializer.toJson<String?>(button),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  ControllerBindingRuleRow copyWith({
    String? scope,
    String? scopeValue,
    String? action,
    Value<String?> button = const Value.absent(),
    DateTime? updatedAt,
  }) => ControllerBindingRuleRow(
    scope: scope ?? this.scope,
    scopeValue: scopeValue ?? this.scopeValue,
    action: action ?? this.action,
    button: button.present ? button.value : this.button,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  ControllerBindingRuleRow copyWithCompanion(
    ControllerBindingRulesCompanion data,
  ) {
    return ControllerBindingRuleRow(
      scope: data.scope.present ? data.scope.value : this.scope,
      scopeValue: data.scopeValue.present
          ? data.scopeValue.value
          : this.scopeValue,
      action: data.action.present ? data.action.value : this.action,
      button: data.button.present ? data.button.value : this.button,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ControllerBindingRuleRow(')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('action: $action, ')
          ..write('button: $button, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(scope, scopeValue, action, button, updatedAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ControllerBindingRuleRow &&
          other.scope == this.scope &&
          other.scopeValue == this.scopeValue &&
          other.action == this.action &&
          other.button == this.button &&
          other.updatedAt == this.updatedAt);
}

class ControllerBindingRulesCompanion
    extends UpdateCompanion<ControllerBindingRuleRow> {
  final Value<String> scope;
  final Value<String> scopeValue;
  final Value<String> action;
  final Value<String?> button;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const ControllerBindingRulesCompanion({
    this.scope = const Value.absent(),
    this.scopeValue = const Value.absent(),
    this.action = const Value.absent(),
    this.button = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ControllerBindingRulesCompanion.insert({
    required String scope,
    required String scopeValue,
    required String action,
    this.button = const Value.absent(),
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : scope = Value(scope),
       scopeValue = Value(scopeValue),
       action = Value(action),
       updatedAt = Value(updatedAt);
  static Insertable<ControllerBindingRuleRow> custom({
    Expression<String>? scope,
    Expression<String>? scopeValue,
    Expression<String>? action,
    Expression<String>? button,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (scope != null) 'scope': scope,
      if (scopeValue != null) 'scope_value': scopeValue,
      if (action != null) 'action': action,
      if (button != null) 'button': button,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ControllerBindingRulesCompanion copyWith({
    Value<String>? scope,
    Value<String>? scopeValue,
    Value<String>? action,
    Value<String?>? button,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return ControllerBindingRulesCompanion(
      scope: scope ?? this.scope,
      scopeValue: scopeValue ?? this.scopeValue,
      action: action ?? this.action,
      button: button ?? this.button,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (scope.present) {
      map['scope'] = Variable<String>(scope.value);
    }
    if (scopeValue.present) {
      map['scope_value'] = Variable<String>(scopeValue.value);
    }
    if (action.present) {
      map['action'] = Variable<String>(action.value);
    }
    if (button.present) {
      map['button'] = Variable<String>(button.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ControllerBindingRulesCompanion(')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('action: $action, ')
          ..write('button: $button, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ControllerMappingProfilesTable extends ControllerMappingProfiles
    with
        TableInfo<
          $ControllerMappingProfilesTable,
          ControllerMappingProfileRow
        > {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ControllerMappingProfilesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id)',
    ),
  );
  static const VerificationMeta _sdlGuidMeta = const VerificationMeta(
    'sdlGuid',
  );
  @override
  late final GeneratedColumn<String> sdlGuid = GeneratedColumn<String>(
    'sdl_guid',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _displayNameMeta = const VerificationMeta(
    'displayName',
  );
  @override
  late final GeneratedColumn<String> displayName = GeneratedColumn<String>(
    'display_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _templateIdMeta = const VerificationMeta(
    'templateId',
  );
  @override
  late final GeneratedColumn<String> templateId = GeneratedColumn<String>(
    'template_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    sdlGuid,
    displayName,
    templateId,
    createdAt,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'controller_mapping_profiles';
  @override
  VerificationContext validateIntegrity(
    Insertable<ControllerMappingProfileRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('sdl_guid')) {
      context.handle(
        _sdlGuidMeta,
        sdlGuid.isAcceptableOrUnknown(data['sdl_guid']!, _sdlGuidMeta),
      );
    } else if (isInserting) {
      context.missing(_sdlGuidMeta);
    }
    if (data.containsKey('display_name')) {
      context.handle(
        _displayNameMeta,
        displayName.isAcceptableOrUnknown(
          data['display_name']!,
          _displayNameMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_displayNameMeta);
    }
    if (data.containsKey('template_id')) {
      context.handle(
        _templateIdMeta,
        templateId.isAcceptableOrUnknown(data['template_id']!, _templateIdMeta),
      );
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {localProfileId, sdlGuid};
  @override
  ControllerMappingProfileRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ControllerMappingProfileRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      sdlGuid: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sdl_guid'],
      )!,
      displayName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}display_name'],
      )!,
      templateId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}template_id'],
      ),
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $ControllerMappingProfilesTable createAlias(String alias) {
    return $ControllerMappingProfilesTable(attachedDatabase, alias);
  }
}

class ControllerMappingProfileRow extends DataClass
    implements Insertable<ControllerMappingProfileRow> {
  final String localProfileId;
  final String sdlGuid;
  final String displayName;
  final String? templateId;
  final DateTime createdAt;
  final DateTime updatedAt;
  const ControllerMappingProfileRow({
    required this.localProfileId,
    required this.sdlGuid,
    required this.displayName,
    this.templateId,
    required this.createdAt,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['sdl_guid'] = Variable<String>(sdlGuid);
    map['display_name'] = Variable<String>(displayName);
    if (!nullToAbsent || templateId != null) {
      map['template_id'] = Variable<String>(templateId);
    }
    map['created_at'] = Variable<DateTime>(createdAt);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  ControllerMappingProfilesCompanion toCompanion(bool nullToAbsent) {
    return ControllerMappingProfilesCompanion(
      localProfileId: Value(localProfileId),
      sdlGuid: Value(sdlGuid),
      displayName: Value(displayName),
      templateId: templateId == null && nullToAbsent
          ? const Value.absent()
          : Value(templateId),
      createdAt: Value(createdAt),
      updatedAt: Value(updatedAt),
    );
  }

  factory ControllerMappingProfileRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ControllerMappingProfileRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      sdlGuid: serializer.fromJson<String>(json['sdlGuid']),
      displayName: serializer.fromJson<String>(json['displayName']),
      templateId: serializer.fromJson<String?>(json['templateId']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'sdlGuid': serializer.toJson<String>(sdlGuid),
      'displayName': serializer.toJson<String>(displayName),
      'templateId': serializer.toJson<String?>(templateId),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  ControllerMappingProfileRow copyWith({
    String? localProfileId,
    String? sdlGuid,
    String? displayName,
    Value<String?> templateId = const Value.absent(),
    DateTime? createdAt,
    DateTime? updatedAt,
  }) => ControllerMappingProfileRow(
    localProfileId: localProfileId ?? this.localProfileId,
    sdlGuid: sdlGuid ?? this.sdlGuid,
    displayName: displayName ?? this.displayName,
    templateId: templateId.present ? templateId.value : this.templateId,
    createdAt: createdAt ?? this.createdAt,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  ControllerMappingProfileRow copyWithCompanion(
    ControllerMappingProfilesCompanion data,
  ) {
    return ControllerMappingProfileRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      sdlGuid: data.sdlGuid.present ? data.sdlGuid.value : this.sdlGuid,
      displayName: data.displayName.present
          ? data.displayName.value
          : this.displayName,
      templateId: data.templateId.present
          ? data.templateId.value
          : this.templateId,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ControllerMappingProfileRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('displayName: $displayName, ')
          ..write('templateId: $templateId, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    localProfileId,
    sdlGuid,
    displayName,
    templateId,
    createdAt,
    updatedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ControllerMappingProfileRow &&
          other.localProfileId == this.localProfileId &&
          other.sdlGuid == this.sdlGuid &&
          other.displayName == this.displayName &&
          other.templateId == this.templateId &&
          other.createdAt == this.createdAt &&
          other.updatedAt == this.updatedAt);
}

class ControllerMappingProfilesCompanion
    extends UpdateCompanion<ControllerMappingProfileRow> {
  final Value<String> localProfileId;
  final Value<String> sdlGuid;
  final Value<String> displayName;
  final Value<String?> templateId;
  final Value<DateTime> createdAt;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const ControllerMappingProfilesCompanion({
    this.localProfileId = const Value.absent(),
    this.sdlGuid = const Value.absent(),
    this.displayName = const Value.absent(),
    this.templateId = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ControllerMappingProfilesCompanion.insert({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    this.templateId = const Value.absent(),
    required DateTime createdAt,
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       sdlGuid = Value(sdlGuid),
       displayName = Value(displayName),
       createdAt = Value(createdAt),
       updatedAt = Value(updatedAt);
  static Insertable<ControllerMappingProfileRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? sdlGuid,
    Expression<String>? displayName,
    Expression<String>? templateId,
    Expression<DateTime>? createdAt,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (sdlGuid != null) 'sdl_guid': sdlGuid,
      if (displayName != null) 'display_name': displayName,
      if (templateId != null) 'template_id': templateId,
      if (createdAt != null) 'created_at': createdAt,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ControllerMappingProfilesCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? sdlGuid,
    Value<String>? displayName,
    Value<String?>? templateId,
    Value<DateTime>? createdAt,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return ControllerMappingProfilesCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      sdlGuid: sdlGuid ?? this.sdlGuid,
      displayName: displayName ?? this.displayName,
      templateId: templateId ?? this.templateId,
      createdAt: createdAt ?? this.createdAt,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (sdlGuid.present) {
      map['sdl_guid'] = Variable<String>(sdlGuid.value);
    }
    if (displayName.present) {
      map['display_name'] = Variable<String>(displayName.value);
    }
    if (templateId.present) {
      map['template_id'] = Variable<String>(templateId.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ControllerMappingProfilesCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('displayName: $displayName, ')
          ..write('templateId: $templateId, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ControllerProfileBindingRulesTable extends ControllerProfileBindingRules
    with
        TableInfo<
          $ControllerProfileBindingRulesTable,
          ControllerProfileBindingRuleRow
        > {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ControllerProfileBindingRulesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _localProfileIdMeta = const VerificationMeta(
    'localProfileId',
  );
  @override
  late final GeneratedColumn<String> localProfileId = GeneratedColumn<String>(
    'local_profile_id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'REFERENCES local_profiles (id)',
    ),
  );
  static const VerificationMeta _sdlGuidMeta = const VerificationMeta(
    'sdlGuid',
  );
  @override
  late final GeneratedColumn<String> sdlGuid = GeneratedColumn<String>(
    'sdl_guid',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _scopeMeta = const VerificationMeta('scope');
  @override
  late final GeneratedColumn<String> scope = GeneratedColumn<String>(
    'scope',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _scopeValueMeta = const VerificationMeta(
    'scopeValue',
  );
  @override
  late final GeneratedColumn<String> scopeValue = GeneratedColumn<String>(
    'scope_value',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _actionMeta = const VerificationMeta('action');
  @override
  late final GeneratedColumn<String> action = GeneratedColumn<String>(
    'action',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _buttonMeta = const VerificationMeta('button');
  @override
  late final GeneratedColumn<String> button = GeneratedColumn<String>(
    'button',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    localProfileId,
    sdlGuid,
    scope,
    scopeValue,
    action,
    button,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'controller_profile_binding_rules';
  @override
  VerificationContext validateIntegrity(
    Insertable<ControllerProfileBindingRuleRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('local_profile_id')) {
      context.handle(
        _localProfileIdMeta,
        localProfileId.isAcceptableOrUnknown(
          data['local_profile_id']!,
          _localProfileIdMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_localProfileIdMeta);
    }
    if (data.containsKey('sdl_guid')) {
      context.handle(
        _sdlGuidMeta,
        sdlGuid.isAcceptableOrUnknown(data['sdl_guid']!, _sdlGuidMeta),
      );
    } else if (isInserting) {
      context.missing(_sdlGuidMeta);
    }
    if (data.containsKey('scope')) {
      context.handle(
        _scopeMeta,
        scope.isAcceptableOrUnknown(data['scope']!, _scopeMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeMeta);
    }
    if (data.containsKey('scope_value')) {
      context.handle(
        _scopeValueMeta,
        scopeValue.isAcceptableOrUnknown(data['scope_value']!, _scopeValueMeta),
      );
    } else if (isInserting) {
      context.missing(_scopeValueMeta);
    }
    if (data.containsKey('action')) {
      context.handle(
        _actionMeta,
        action.isAcceptableOrUnknown(data['action']!, _actionMeta),
      );
    } else if (isInserting) {
      context.missing(_actionMeta);
    }
    if (data.containsKey('button')) {
      context.handle(
        _buttonMeta,
        button.isAcceptableOrUnknown(data['button']!, _buttonMeta),
      );
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {
    localProfileId,
    sdlGuid,
    scope,
    scopeValue,
    action,
  };
  @override
  ControllerProfileBindingRuleRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ControllerProfileBindingRuleRow(
      localProfileId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_profile_id'],
      )!,
      sdlGuid: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sdl_guid'],
      )!,
      scope: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope'],
      )!,
      scopeValue: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}scope_value'],
      )!,
      action: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}action'],
      )!,
      button: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}button'],
      ),
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $ControllerProfileBindingRulesTable createAlias(String alias) {
    return $ControllerProfileBindingRulesTable(attachedDatabase, alias);
  }
}

class ControllerProfileBindingRuleRow extends DataClass
    implements Insertable<ControllerProfileBindingRuleRow> {
  final String localProfileId;
  final String sdlGuid;
  final String scope;
  final String scopeValue;
  final String action;
  final String? button;
  final DateTime updatedAt;
  const ControllerProfileBindingRuleRow({
    required this.localProfileId,
    required this.sdlGuid,
    required this.scope,
    required this.scopeValue,
    required this.action,
    this.button,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['local_profile_id'] = Variable<String>(localProfileId);
    map['sdl_guid'] = Variable<String>(sdlGuid);
    map['scope'] = Variable<String>(scope);
    map['scope_value'] = Variable<String>(scopeValue);
    map['action'] = Variable<String>(action);
    if (!nullToAbsent || button != null) {
      map['button'] = Variable<String>(button);
    }
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  ControllerProfileBindingRulesCompanion toCompanion(bool nullToAbsent) {
    return ControllerProfileBindingRulesCompanion(
      localProfileId: Value(localProfileId),
      sdlGuid: Value(sdlGuid),
      scope: Value(scope),
      scopeValue: Value(scopeValue),
      action: Value(action),
      button: button == null && nullToAbsent
          ? const Value.absent()
          : Value(button),
      updatedAt: Value(updatedAt),
    );
  }

  factory ControllerProfileBindingRuleRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ControllerProfileBindingRuleRow(
      localProfileId: serializer.fromJson<String>(json['localProfileId']),
      sdlGuid: serializer.fromJson<String>(json['sdlGuid']),
      scope: serializer.fromJson<String>(json['scope']),
      scopeValue: serializer.fromJson<String>(json['scopeValue']),
      action: serializer.fromJson<String>(json['action']),
      button: serializer.fromJson<String?>(json['button']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'localProfileId': serializer.toJson<String>(localProfileId),
      'sdlGuid': serializer.toJson<String>(sdlGuid),
      'scope': serializer.toJson<String>(scope),
      'scopeValue': serializer.toJson<String>(scopeValue),
      'action': serializer.toJson<String>(action),
      'button': serializer.toJson<String?>(button),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  ControllerProfileBindingRuleRow copyWith({
    String? localProfileId,
    String? sdlGuid,
    String? scope,
    String? scopeValue,
    String? action,
    Value<String?> button = const Value.absent(),
    DateTime? updatedAt,
  }) => ControllerProfileBindingRuleRow(
    localProfileId: localProfileId ?? this.localProfileId,
    sdlGuid: sdlGuid ?? this.sdlGuid,
    scope: scope ?? this.scope,
    scopeValue: scopeValue ?? this.scopeValue,
    action: action ?? this.action,
    button: button.present ? button.value : this.button,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  ControllerProfileBindingRuleRow copyWithCompanion(
    ControllerProfileBindingRulesCompanion data,
  ) {
    return ControllerProfileBindingRuleRow(
      localProfileId: data.localProfileId.present
          ? data.localProfileId.value
          : this.localProfileId,
      sdlGuid: data.sdlGuid.present ? data.sdlGuid.value : this.sdlGuid,
      scope: data.scope.present ? data.scope.value : this.scope,
      scopeValue: data.scopeValue.present
          ? data.scopeValue.value
          : this.scopeValue,
      action: data.action.present ? data.action.value : this.action,
      button: data.button.present ? data.button.value : this.button,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ControllerProfileBindingRuleRow(')
          ..write('localProfileId: $localProfileId, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('action: $action, ')
          ..write('button: $button, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    localProfileId,
    sdlGuid,
    scope,
    scopeValue,
    action,
    button,
    updatedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ControllerProfileBindingRuleRow &&
          other.localProfileId == this.localProfileId &&
          other.sdlGuid == this.sdlGuid &&
          other.scope == this.scope &&
          other.scopeValue == this.scopeValue &&
          other.action == this.action &&
          other.button == this.button &&
          other.updatedAt == this.updatedAt);
}

class ControllerProfileBindingRulesCompanion
    extends UpdateCompanion<ControllerProfileBindingRuleRow> {
  final Value<String> localProfileId;
  final Value<String> sdlGuid;
  final Value<String> scope;
  final Value<String> scopeValue;
  final Value<String> action;
  final Value<String?> button;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const ControllerProfileBindingRulesCompanion({
    this.localProfileId = const Value.absent(),
    this.sdlGuid = const Value.absent(),
    this.scope = const Value.absent(),
    this.scopeValue = const Value.absent(),
    this.action = const Value.absent(),
    this.button = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ControllerProfileBindingRulesCompanion.insert({
    required String localProfileId,
    required String sdlGuid,
    required String scope,
    required String scopeValue,
    required String action,
    this.button = const Value.absent(),
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : localProfileId = Value(localProfileId),
       sdlGuid = Value(sdlGuid),
       scope = Value(scope),
       scopeValue = Value(scopeValue),
       action = Value(action),
       updatedAt = Value(updatedAt);
  static Insertable<ControllerProfileBindingRuleRow> custom({
    Expression<String>? localProfileId,
    Expression<String>? sdlGuid,
    Expression<String>? scope,
    Expression<String>? scopeValue,
    Expression<String>? action,
    Expression<String>? button,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (localProfileId != null) 'local_profile_id': localProfileId,
      if (sdlGuid != null) 'sdl_guid': sdlGuid,
      if (scope != null) 'scope': scope,
      if (scopeValue != null) 'scope_value': scopeValue,
      if (action != null) 'action': action,
      if (button != null) 'button': button,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ControllerProfileBindingRulesCompanion copyWith({
    Value<String>? localProfileId,
    Value<String>? sdlGuid,
    Value<String>? scope,
    Value<String>? scopeValue,
    Value<String>? action,
    Value<String?>? button,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return ControllerProfileBindingRulesCompanion(
      localProfileId: localProfileId ?? this.localProfileId,
      sdlGuid: sdlGuid ?? this.sdlGuid,
      scope: scope ?? this.scope,
      scopeValue: scopeValue ?? this.scopeValue,
      action: action ?? this.action,
      button: button ?? this.button,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (localProfileId.present) {
      map['local_profile_id'] = Variable<String>(localProfileId.value);
    }
    if (sdlGuid.present) {
      map['sdl_guid'] = Variable<String>(sdlGuid.value);
    }
    if (scope.present) {
      map['scope'] = Variable<String>(scope.value);
    }
    if (scopeValue.present) {
      map['scope_value'] = Variable<String>(scopeValue.value);
    }
    if (action.present) {
      map['action'] = Variable<String>(action.value);
    }
    if (button.present) {
      map['button'] = Variable<String>(button.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ControllerProfileBindingRulesCompanion(')
          ..write('localProfileId: $localProfileId, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('scope: $scope, ')
          ..write('scopeValue: $scopeValue, ')
          ..write('action: $action, ')
          ..write('button: $button, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ControllerPreferencesRowsTable extends ControllerPreferencesRows
    with TableInfo<$ControllerPreferencesRowsTable, ControllerPreferencesRow> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ControllerPreferencesRowsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<int> id = GeneratedColumn<int>(
    'id',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _templateIdMeta = const VerificationMeta(
    'templateId',
  );
  @override
  late final GeneratedColumn<String> templateId = GeneratedColumn<String>(
    'template_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [id, templateId, updatedAt];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'controller_preferences_rows';
  @override
  VerificationContext validateIntegrity(
    Insertable<ControllerPreferencesRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    }
    if (data.containsKey('template_id')) {
      context.handle(
        _templateIdMeta,
        templateId.isAcceptableOrUnknown(data['template_id']!, _templateIdMeta),
      );
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  ControllerPreferencesRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ControllerPreferencesRow(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}id'],
      )!,
      templateId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}template_id'],
      ),
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $ControllerPreferencesRowsTable createAlias(String alias) {
    return $ControllerPreferencesRowsTable(attachedDatabase, alias);
  }
}

class ControllerPreferencesRow extends DataClass
    implements Insertable<ControllerPreferencesRow> {
  final int id;
  final String? templateId;
  final DateTime updatedAt;
  const ControllerPreferencesRow({
    required this.id,
    this.templateId,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<int>(id);
    if (!nullToAbsent || templateId != null) {
      map['template_id'] = Variable<String>(templateId);
    }
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  ControllerPreferencesRowsCompanion toCompanion(bool nullToAbsent) {
    return ControllerPreferencesRowsCompanion(
      id: Value(id),
      templateId: templateId == null && nullToAbsent
          ? const Value.absent()
          : Value(templateId),
      updatedAt: Value(updatedAt),
    );
  }

  factory ControllerPreferencesRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ControllerPreferencesRow(
      id: serializer.fromJson<int>(json['id']),
      templateId: serializer.fromJson<String?>(json['templateId']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<int>(id),
      'templateId': serializer.toJson<String?>(templateId),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  ControllerPreferencesRow copyWith({
    int? id,
    Value<String?> templateId = const Value.absent(),
    DateTime? updatedAt,
  }) => ControllerPreferencesRow(
    id: id ?? this.id,
    templateId: templateId.present ? templateId.value : this.templateId,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  ControllerPreferencesRow copyWithCompanion(
    ControllerPreferencesRowsCompanion data,
  ) {
    return ControllerPreferencesRow(
      id: data.id.present ? data.id.value : this.id,
      templateId: data.templateId.present
          ? data.templateId.value
          : this.templateId,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ControllerPreferencesRow(')
          ..write('id: $id, ')
          ..write('templateId: $templateId, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(id, templateId, updatedAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ControllerPreferencesRow &&
          other.id == this.id &&
          other.templateId == this.templateId &&
          other.updatedAt == this.updatedAt);
}

class ControllerPreferencesRowsCompanion
    extends UpdateCompanion<ControllerPreferencesRow> {
  final Value<int> id;
  final Value<String?> templateId;
  final Value<DateTime> updatedAt;
  const ControllerPreferencesRowsCompanion({
    this.id = const Value.absent(),
    this.templateId = const Value.absent(),
    this.updatedAt = const Value.absent(),
  });
  ControllerPreferencesRowsCompanion.insert({
    this.id = const Value.absent(),
    this.templateId = const Value.absent(),
    required DateTime updatedAt,
  }) : updatedAt = Value(updatedAt);
  static Insertable<ControllerPreferencesRow> custom({
    Expression<int>? id,
    Expression<String>? templateId,
    Expression<DateTime>? updatedAt,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (templateId != null) 'template_id': templateId,
      if (updatedAt != null) 'updated_at': updatedAt,
    });
  }

  ControllerPreferencesRowsCompanion copyWith({
    Value<int>? id,
    Value<String?>? templateId,
    Value<DateTime>? updatedAt,
  }) {
    return ControllerPreferencesRowsCompanion(
      id: id ?? this.id,
      templateId: templateId ?? this.templateId,
      updatedAt: updatedAt ?? this.updatedAt,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<int>(id.value);
    }
    if (templateId.present) {
      map['template_id'] = Variable<String>(templateId.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ControllerPreferencesRowsCompanion(')
          ..write('id: $id, ')
          ..write('templateId: $templateId, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }
}

class $ControllerHardwareMappingsTable extends ControllerHardwareMappings
    with
        TableInfo<
          $ControllerHardwareMappingsTable,
          ControllerHardwareMappingRow
        > {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ControllerHardwareMappingsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _sdlPlatformMeta = const VerificationMeta(
    'sdlPlatform',
  );
  @override
  late final GeneratedColumn<String> sdlPlatform = GeneratedColumn<String>(
    'sdl_platform',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _sdlGuidMeta = const VerificationMeta(
    'sdlGuid',
  );
  @override
  late final GeneratedColumn<String> sdlGuid = GeneratedColumn<String>(
    'sdl_guid',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _displayNameMeta = const VerificationMeta(
    'displayName',
  );
  @override
  late final GeneratedColumn<String> displayName = GeneratedColumn<String>(
    'display_name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _mappingFormatVersionMeta =
      const VerificationMeta('mappingFormatVersion');
  @override
  late final GeneratedColumn<int> mappingFormatVersion = GeneratedColumn<int>(
    'mapping_format_version',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _mappingDataMeta = const VerificationMeta(
    'mappingData',
  );
  @override
  late final GeneratedColumn<String> mappingData = GeneratedColumn<String>(
    'mapping_data',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _updatedAtMeta = const VerificationMeta(
    'updatedAt',
  );
  @override
  late final GeneratedColumn<DateTime> updatedAt = GeneratedColumn<DateTime>(
    'updated_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    sdlPlatform,
    sdlGuid,
    displayName,
    mappingFormatVersion,
    mappingData,
    createdAt,
    updatedAt,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'controller_hardware_mappings';
  @override
  VerificationContext validateIntegrity(
    Insertable<ControllerHardwareMappingRow> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('sdl_platform')) {
      context.handle(
        _sdlPlatformMeta,
        sdlPlatform.isAcceptableOrUnknown(
          data['sdl_platform']!,
          _sdlPlatformMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_sdlPlatformMeta);
    }
    if (data.containsKey('sdl_guid')) {
      context.handle(
        _sdlGuidMeta,
        sdlGuid.isAcceptableOrUnknown(data['sdl_guid']!, _sdlGuidMeta),
      );
    } else if (isInserting) {
      context.missing(_sdlGuidMeta);
    }
    if (data.containsKey('display_name')) {
      context.handle(
        _displayNameMeta,
        displayName.isAcceptableOrUnknown(
          data['display_name']!,
          _displayNameMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_displayNameMeta);
    }
    if (data.containsKey('mapping_format_version')) {
      context.handle(
        _mappingFormatVersionMeta,
        mappingFormatVersion.isAcceptableOrUnknown(
          data['mapping_format_version']!,
          _mappingFormatVersionMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_mappingFormatVersionMeta);
    }
    if (data.containsKey('mapping_data')) {
      context.handle(
        _mappingDataMeta,
        mappingData.isAcceptableOrUnknown(
          data['mapping_data']!,
          _mappingDataMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_mappingDataMeta);
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('updated_at')) {
      context.handle(
        _updatedAtMeta,
        updatedAt.isAcceptableOrUnknown(data['updated_at']!, _updatedAtMeta),
      );
    } else if (isInserting) {
      context.missing(_updatedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {sdlPlatform, sdlGuid};
  @override
  ControllerHardwareMappingRow map(
    Map<String, dynamic> data, {
    String? tablePrefix,
  }) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ControllerHardwareMappingRow(
      sdlPlatform: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sdl_platform'],
      )!,
      sdlGuid: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sdl_guid'],
      )!,
      displayName: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}display_name'],
      )!,
      mappingFormatVersion: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}mapping_format_version'],
      )!,
      mappingData: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}mapping_data'],
      )!,
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      updatedAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}updated_at'],
      )!,
    );
  }

  @override
  $ControllerHardwareMappingsTable createAlias(String alias) {
    return $ControllerHardwareMappingsTable(attachedDatabase, alias);
  }
}

class ControllerHardwareMappingRow extends DataClass
    implements Insertable<ControllerHardwareMappingRow> {
  final String sdlPlatform;
  final String sdlGuid;
  final String displayName;
  final int mappingFormatVersion;
  final String mappingData;
  final DateTime createdAt;
  final DateTime updatedAt;
  const ControllerHardwareMappingRow({
    required this.sdlPlatform,
    required this.sdlGuid,
    required this.displayName,
    required this.mappingFormatVersion,
    required this.mappingData,
    required this.createdAt,
    required this.updatedAt,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['sdl_platform'] = Variable<String>(sdlPlatform);
    map['sdl_guid'] = Variable<String>(sdlGuid);
    map['display_name'] = Variable<String>(displayName);
    map['mapping_format_version'] = Variable<int>(mappingFormatVersion);
    map['mapping_data'] = Variable<String>(mappingData);
    map['created_at'] = Variable<DateTime>(createdAt);
    map['updated_at'] = Variable<DateTime>(updatedAt);
    return map;
  }

  ControllerHardwareMappingsCompanion toCompanion(bool nullToAbsent) {
    return ControllerHardwareMappingsCompanion(
      sdlPlatform: Value(sdlPlatform),
      sdlGuid: Value(sdlGuid),
      displayName: Value(displayName),
      mappingFormatVersion: Value(mappingFormatVersion),
      mappingData: Value(mappingData),
      createdAt: Value(createdAt),
      updatedAt: Value(updatedAt),
    );
  }

  factory ControllerHardwareMappingRow.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ControllerHardwareMappingRow(
      sdlPlatform: serializer.fromJson<String>(json['sdlPlatform']),
      sdlGuid: serializer.fromJson<String>(json['sdlGuid']),
      displayName: serializer.fromJson<String>(json['displayName']),
      mappingFormatVersion: serializer.fromJson<int>(
        json['mappingFormatVersion'],
      ),
      mappingData: serializer.fromJson<String>(json['mappingData']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      updatedAt: serializer.fromJson<DateTime>(json['updatedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'sdlPlatform': serializer.toJson<String>(sdlPlatform),
      'sdlGuid': serializer.toJson<String>(sdlGuid),
      'displayName': serializer.toJson<String>(displayName),
      'mappingFormatVersion': serializer.toJson<int>(mappingFormatVersion),
      'mappingData': serializer.toJson<String>(mappingData),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'updatedAt': serializer.toJson<DateTime>(updatedAt),
    };
  }

  ControllerHardwareMappingRow copyWith({
    String? sdlPlatform,
    String? sdlGuid,
    String? displayName,
    int? mappingFormatVersion,
    String? mappingData,
    DateTime? createdAt,
    DateTime? updatedAt,
  }) => ControllerHardwareMappingRow(
    sdlPlatform: sdlPlatform ?? this.sdlPlatform,
    sdlGuid: sdlGuid ?? this.sdlGuid,
    displayName: displayName ?? this.displayName,
    mappingFormatVersion: mappingFormatVersion ?? this.mappingFormatVersion,
    mappingData: mappingData ?? this.mappingData,
    createdAt: createdAt ?? this.createdAt,
    updatedAt: updatedAt ?? this.updatedAt,
  );
  ControllerHardwareMappingRow copyWithCompanion(
    ControllerHardwareMappingsCompanion data,
  ) {
    return ControllerHardwareMappingRow(
      sdlPlatform: data.sdlPlatform.present
          ? data.sdlPlatform.value
          : this.sdlPlatform,
      sdlGuid: data.sdlGuid.present ? data.sdlGuid.value : this.sdlGuid,
      displayName: data.displayName.present
          ? data.displayName.value
          : this.displayName,
      mappingFormatVersion: data.mappingFormatVersion.present
          ? data.mappingFormatVersion.value
          : this.mappingFormatVersion,
      mappingData: data.mappingData.present
          ? data.mappingData.value
          : this.mappingData,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      updatedAt: data.updatedAt.present ? data.updatedAt.value : this.updatedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ControllerHardwareMappingRow(')
          ..write('sdlPlatform: $sdlPlatform, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('displayName: $displayName, ')
          ..write('mappingFormatVersion: $mappingFormatVersion, ')
          ..write('mappingData: $mappingData, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    sdlPlatform,
    sdlGuid,
    displayName,
    mappingFormatVersion,
    mappingData,
    createdAt,
    updatedAt,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ControllerHardwareMappingRow &&
          other.sdlPlatform == this.sdlPlatform &&
          other.sdlGuid == this.sdlGuid &&
          other.displayName == this.displayName &&
          other.mappingFormatVersion == this.mappingFormatVersion &&
          other.mappingData == this.mappingData &&
          other.createdAt == this.createdAt &&
          other.updatedAt == this.updatedAt);
}

class ControllerHardwareMappingsCompanion
    extends UpdateCompanion<ControllerHardwareMappingRow> {
  final Value<String> sdlPlatform;
  final Value<String> sdlGuid;
  final Value<String> displayName;
  final Value<int> mappingFormatVersion;
  final Value<String> mappingData;
  final Value<DateTime> createdAt;
  final Value<DateTime> updatedAt;
  final Value<int> rowid;
  const ControllerHardwareMappingsCompanion({
    this.sdlPlatform = const Value.absent(),
    this.sdlGuid = const Value.absent(),
    this.displayName = const Value.absent(),
    this.mappingFormatVersion = const Value.absent(),
    this.mappingData = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.updatedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ControllerHardwareMappingsCompanion.insert({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required int mappingFormatVersion,
    required String mappingData,
    required DateTime createdAt,
    required DateTime updatedAt,
    this.rowid = const Value.absent(),
  }) : sdlPlatform = Value(sdlPlatform),
       sdlGuid = Value(sdlGuid),
       displayName = Value(displayName),
       mappingFormatVersion = Value(mappingFormatVersion),
       mappingData = Value(mappingData),
       createdAt = Value(createdAt),
       updatedAt = Value(updatedAt);
  static Insertable<ControllerHardwareMappingRow> custom({
    Expression<String>? sdlPlatform,
    Expression<String>? sdlGuid,
    Expression<String>? displayName,
    Expression<int>? mappingFormatVersion,
    Expression<String>? mappingData,
    Expression<DateTime>? createdAt,
    Expression<DateTime>? updatedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (sdlPlatform != null) 'sdl_platform': sdlPlatform,
      if (sdlGuid != null) 'sdl_guid': sdlGuid,
      if (displayName != null) 'display_name': displayName,
      if (mappingFormatVersion != null)
        'mapping_format_version': mappingFormatVersion,
      if (mappingData != null) 'mapping_data': mappingData,
      if (createdAt != null) 'created_at': createdAt,
      if (updatedAt != null) 'updated_at': updatedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ControllerHardwareMappingsCompanion copyWith({
    Value<String>? sdlPlatform,
    Value<String>? sdlGuid,
    Value<String>? displayName,
    Value<int>? mappingFormatVersion,
    Value<String>? mappingData,
    Value<DateTime>? createdAt,
    Value<DateTime>? updatedAt,
    Value<int>? rowid,
  }) {
    return ControllerHardwareMappingsCompanion(
      sdlPlatform: sdlPlatform ?? this.sdlPlatform,
      sdlGuid: sdlGuid ?? this.sdlGuid,
      displayName: displayName ?? this.displayName,
      mappingFormatVersion: mappingFormatVersion ?? this.mappingFormatVersion,
      mappingData: mappingData ?? this.mappingData,
      createdAt: createdAt ?? this.createdAt,
      updatedAt: updatedAt ?? this.updatedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (sdlPlatform.present) {
      map['sdl_platform'] = Variable<String>(sdlPlatform.value);
    }
    if (sdlGuid.present) {
      map['sdl_guid'] = Variable<String>(sdlGuid.value);
    }
    if (displayName.present) {
      map['display_name'] = Variable<String>(displayName.value);
    }
    if (mappingFormatVersion.present) {
      map['mapping_format_version'] = Variable<int>(mappingFormatVersion.value);
    }
    if (mappingData.present) {
      map['mapping_data'] = Variable<String>(mappingData.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (updatedAt.present) {
      map['updated_at'] = Variable<DateTime>(updatedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ControllerHardwareMappingsCompanion(')
          ..write('sdlPlatform: $sdlPlatform, ')
          ..write('sdlGuid: $sdlGuid, ')
          ..write('displayName: $displayName, ')
          ..write('mappingFormatVersion: $mappingFormatVersion, ')
          ..write('mappingData: $mappingData, ')
          ..write('createdAt: $createdAt, ')
          ..write('updatedAt: $updatedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

abstract class _$AppDatabase extends GeneratedDatabase {
  _$AppDatabase(QueryExecutor e) : super(e);
  $AppDatabaseManager get managers => $AppDatabaseManager(this);
  late final $ServerConnectionsTable serverConnections =
      $ServerConnectionsTable(this);
  late final $LocalProfilesTable localProfiles = $LocalProfilesTable(this);
  late final $PendingServerLocatorsTable pendingServerLocators =
      $PendingServerLocatorsTable(this);
  late final $RomdAccountLinksTable romdAccountLinks = $RomdAccountLinksTable(
    this,
  );
  late final $ProfileLocalGamesTable profileLocalGames =
      $ProfileLocalGamesTable(this);
  late final $ProfilePlayHistoriesTable profilePlayHistories =
      $ProfilePlayHistoriesTable(this);
  late final $PlayActivitySyncPreferencesTable playActivitySyncPreferences =
      $PlayActivitySyncPreferencesTable(this);
  late final $LocalPlaySessionsTable localPlaySessions =
      $LocalPlaySessionsTable(this);
  late final $PlayActivityOutboxTable playActivityOutbox =
      $PlayActivityOutboxTable(this);
  late final $LegacyLocalInstallsTable legacyLocalInstalls =
      $LegacyLocalInstallsTable(this);
  late final $LocalInstallsTable localInstalls = $LocalInstallsTable(this);
  late final $RuntimeOverrideRulesTable runtimeOverrideRules =
      $RuntimeOverrideRulesTable(this);
  late final $ControllerBindingRulesTable controllerBindingRules =
      $ControllerBindingRulesTable(this);
  late final $ControllerMappingProfilesTable controllerMappingProfiles =
      $ControllerMappingProfilesTable(this);
  late final $ControllerProfileBindingRulesTable controllerProfileBindingRules =
      $ControllerProfileBindingRulesTable(this);
  late final $ControllerPreferencesRowsTable controllerPreferencesRows =
      $ControllerPreferencesRowsTable(this);
  late final $ControllerHardwareMappingsTable controllerHardwareMappings =
      $ControllerHardwareMappingsTable(this);
  late final Index profileLocalGamesServerRelease = Index(
    'profile_local_games_server_release',
    'CREATE INDEX profile_local_games_server_release ON profile_local_games (server_instance_id, release_id)',
  );
  late final Index localPlaySessionsProfileServerStarted = Index(
    'local_play_sessions_profile_server_started',
    'CREATE INDEX local_play_sessions_profile_server_started ON local_play_sessions (local_profile_id, server_instance_id, started_at)',
  );
  @override
  Iterable<TableInfo<Table, Object?>> get allTables =>
      allSchemaEntities.whereType<TableInfo<Table, Object?>>();
  @override
  List<DatabaseSchemaEntity> get allSchemaEntities => [
    serverConnections,
    localProfiles,
    pendingServerLocators,
    romdAccountLinks,
    profileLocalGames,
    profilePlayHistories,
    playActivitySyncPreferences,
    localPlaySessions,
    playActivityOutbox,
    legacyLocalInstalls,
    localInstalls,
    runtimeOverrideRules,
    controllerBindingRules,
    controllerMappingProfiles,
    controllerProfileBindingRules,
    controllerPreferencesRows,
    controllerHardwareMappings,
    profileLocalGamesServerRelease,
    localPlaySessionsProfileServerStarted,
  ];
  @override
  StreamQueryUpdateRules get streamUpdateRules => const StreamQueryUpdateRules([
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_profiles',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [TableUpdate('pending_server_locators', kind: UpdateKind.delete)],
    ),
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_profiles',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [TableUpdate('profile_local_games', kind: UpdateKind.delete)],
    ),
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_profiles',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [TableUpdate('profile_play_histories', kind: UpdateKind.delete)],
    ),
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_profiles',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [
        TableUpdate('play_activity_sync_preferences', kind: UpdateKind.delete),
      ],
    ),
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_profiles',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [TableUpdate('local_play_sessions', kind: UpdateKind.delete)],
    ),
    WritePropagation(
      on: TableUpdateQuery.onTableName(
        'local_play_sessions',
        limitUpdateKind: UpdateKind.delete,
      ),
      result: [TableUpdate('play_activity_outbox', kind: UpdateKind.delete)],
    ),
  ]);
}

typedef $$ServerConnectionsTableCreateCompanionBuilder =
    ServerConnectionsCompanion Function({
      required String instanceId,
      required String lastKnownOrigin,
      required DateTime firstSeenAt,
      required DateTime lastSeenAt,
      Value<int> rowid,
    });
typedef $$ServerConnectionsTableUpdateCompanionBuilder =
    ServerConnectionsCompanion Function({
      Value<String> instanceId,
      Value<String> lastKnownOrigin,
      Value<DateTime> firstSeenAt,
      Value<DateTime> lastSeenAt,
      Value<int> rowid,
    });

final class $$ServerConnectionsTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $ServerConnectionsTable,
          ServerConnectionRow
        > {
  $$ServerConnectionsTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static MultiTypedResultKey<$LocalProfilesTable, List<LocalProfileRow>>
  _localProfilesRefsTable(_$AppDatabase db) => MultiTypedResultKey.fromTable(
    db.localProfiles,
    aliasName:
        'server_connections__instance_id__local_profiles__selected_server_instance_id',
  );

  $$LocalProfilesTableProcessedTableManager get localProfilesRefs {
    final manager = $$LocalProfilesTableTableManager($_db, $_db.localProfiles)
        .filter(
          (f) => f.selectedServerInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(_localProfilesRefsTable($_db));
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$RomdAccountLinksTable, List<RomdAccountLinkRow>>
  _romdAccountLinksRefsTable(_$AppDatabase db) => MultiTypedResultKey.fromTable(
    db.romdAccountLinks,
    aliasName:
        'server_connections__instance_id__romd_account_links__server_instance_id',
  );

  $$RomdAccountLinksTableProcessedTableManager get romdAccountLinksRefs {
    final manager =
        $$RomdAccountLinksTableTableManager($_db, $_db.romdAccountLinks).filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _romdAccountLinksRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$ProfileLocalGamesTable, List<ProfileLocalGameRow>>
  _profileLocalGamesRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.profileLocalGames,
    aliasName:
        'server_connections__instance_id__profile_local_games__server_instance_id',
  );

  $$ProfileLocalGamesTableProcessedTableManager get profileLocalGamesRefs {
    final manager =
        $$ProfileLocalGamesTableTableManager(
          $_db,
          $_db.profileLocalGames,
        ).filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _profileLocalGamesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $ProfilePlayHistoriesTable,
    List<ProfilePlayHistoryRow>
  >
  _profilePlayHistoriesRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.profilePlayHistories,
    aliasName:
        'server_connections__instance_id__profile_play_histories__server_instance_id',
  );

  $$ProfilePlayHistoriesTableProcessedTableManager
  get profilePlayHistoriesRefs {
    final manager =
        $$ProfilePlayHistoriesTableTableManager(
          $_db,
          $_db.profilePlayHistories,
        ).filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _profilePlayHistoriesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $PlayActivitySyncPreferencesTable,
    List<PlayActivitySyncPreferenceRow>
  >
  _playActivitySyncPreferencesRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.playActivitySyncPreferences,
    aliasName:
        'server_connections__instance_id__play_activity_sync_preferences__server_instance_id',
  );

  $$PlayActivitySyncPreferencesTableProcessedTableManager
  get playActivitySyncPreferencesRefs {
    final manager =
        $$PlayActivitySyncPreferencesTableTableManager(
          $_db,
          $_db.playActivitySyncPreferences,
        ).filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _playActivitySyncPreferencesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$LocalPlaySessionsTable, List<LocalPlaySessionRow>>
  _localPlaySessionsRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.localPlaySessions,
    aliasName:
        'server_connections__instance_id__local_play_sessions__server_instance_id',
  );

  $$LocalPlaySessionsTableProcessedTableManager get localPlaySessionsRefs {
    final manager =
        $$LocalPlaySessionsTableTableManager(
          $_db,
          $_db.localPlaySessions,
        ).filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _localPlaySessionsRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$LocalInstallsTable, List<LocalInstallRow>>
  _localInstallsRefsTable(_$AppDatabase db) => MultiTypedResultKey.fromTable(
    db.localInstalls,
    aliasName:
        'server_connections__instance_id__local_installs__server_instance_id',
  );

  $$LocalInstallsTableProcessedTableManager get localInstallsRefs {
    final manager = $$LocalInstallsTableTableManager($_db, $_db.localInstalls)
        .filter(
          (f) => f.serverInstanceId.instanceId.sqlEquals(
            $_itemColumn<String>('instance_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(_localInstallsRefsTable($_db));
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }
}

class $$ServerConnectionsTableFilterComposer
    extends Composer<_$AppDatabase, $ServerConnectionsTable> {
  $$ServerConnectionsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get instanceId => $composableBuilder(
    column: $table.instanceId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get lastKnownOrigin => $composableBuilder(
    column: $table.lastKnownOrigin,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get firstSeenAt => $composableBuilder(
    column: $table.firstSeenAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastSeenAt => $composableBuilder(
    column: $table.lastSeenAt,
    builder: (column) => ColumnFilters(column),
  );

  Expression<bool> localProfilesRefs(
    Expression<bool> Function($$LocalProfilesTableFilterComposer f) f,
  ) {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.selectedServerInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> romdAccountLinksRefs(
    Expression<bool> Function($$RomdAccountLinksTableFilterComposer f) f,
  ) {
    final $$RomdAccountLinksTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.romdAccountLinks,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$RomdAccountLinksTableFilterComposer(
            $db: $db,
            $table: $db.romdAccountLinks,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> profileLocalGamesRefs(
    Expression<bool> Function($$ProfileLocalGamesTableFilterComposer f) f,
  ) {
    final $$ProfileLocalGamesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.profileLocalGames,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ProfileLocalGamesTableFilterComposer(
            $db: $db,
            $table: $db.profileLocalGames,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> profilePlayHistoriesRefs(
    Expression<bool> Function($$ProfilePlayHistoriesTableFilterComposer f) f,
  ) {
    final $$ProfilePlayHistoriesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.profilePlayHistories,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ProfilePlayHistoriesTableFilterComposer(
            $db: $db,
            $table: $db.profilePlayHistories,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> playActivitySyncPreferencesRefs(
    Expression<bool> Function(
      $$PlayActivitySyncPreferencesTableFilterComposer f,
    )
    f,
  ) {
    final $$PlayActivitySyncPreferencesTableFilterComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.instanceId,
          referencedTable: $db.playActivitySyncPreferences,
          getReferencedColumn: (t) => t.serverInstanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PlayActivitySyncPreferencesTableFilterComposer(
                $db: $db,
                $table: $db.playActivitySyncPreferences,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<bool> localPlaySessionsRefs(
    Expression<bool> Function($$LocalPlaySessionsTableFilterComposer f) f,
  ) {
    final $$LocalPlaySessionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.localPlaySessions,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalPlaySessionsTableFilterComposer(
            $db: $db,
            $table: $db.localPlaySessions,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> localInstallsRefs(
    Expression<bool> Function($$LocalInstallsTableFilterComposer f) f,
  ) {
    final $$LocalInstallsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.localInstalls,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalInstallsTableFilterComposer(
            $db: $db,
            $table: $db.localInstalls,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }
}

class $$ServerConnectionsTableOrderingComposer
    extends Composer<_$AppDatabase, $ServerConnectionsTable> {
  $$ServerConnectionsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get instanceId => $composableBuilder(
    column: $table.instanceId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get lastKnownOrigin => $composableBuilder(
    column: $table.lastKnownOrigin,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get firstSeenAt => $composableBuilder(
    column: $table.firstSeenAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastSeenAt => $composableBuilder(
    column: $table.lastSeenAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ServerConnectionsTableAnnotationComposer
    extends Composer<_$AppDatabase, $ServerConnectionsTable> {
  $$ServerConnectionsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get instanceId => $composableBuilder(
    column: $table.instanceId,
    builder: (column) => column,
  );

  GeneratedColumn<String> get lastKnownOrigin => $composableBuilder(
    column: $table.lastKnownOrigin,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get firstSeenAt => $composableBuilder(
    column: $table.firstSeenAt,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get lastSeenAt => $composableBuilder(
    column: $table.lastSeenAt,
    builder: (column) => column,
  );

  Expression<T> localProfilesRefs<T extends Object>(
    Expression<T> Function($$LocalProfilesTableAnnotationComposer a) f,
  ) {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.selectedServerInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<T> romdAccountLinksRefs<T extends Object>(
    Expression<T> Function($$RomdAccountLinksTableAnnotationComposer a) f,
  ) {
    final $$RomdAccountLinksTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.romdAccountLinks,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$RomdAccountLinksTableAnnotationComposer(
            $db: $db,
            $table: $db.romdAccountLinks,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<T> profileLocalGamesRefs<T extends Object>(
    Expression<T> Function($$ProfileLocalGamesTableAnnotationComposer a) f,
  ) {
    final $$ProfileLocalGamesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.instanceId,
          referencedTable: $db.profileLocalGames,
          getReferencedColumn: (t) => t.serverInstanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ProfileLocalGamesTableAnnotationComposer(
                $db: $db,
                $table: $db.profileLocalGames,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> profilePlayHistoriesRefs<T extends Object>(
    Expression<T> Function($$ProfilePlayHistoriesTableAnnotationComposer a) f,
  ) {
    final $$ProfilePlayHistoriesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.instanceId,
          referencedTable: $db.profilePlayHistories,
          getReferencedColumn: (t) => t.serverInstanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ProfilePlayHistoriesTableAnnotationComposer(
                $db: $db,
                $table: $db.profilePlayHistories,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> playActivitySyncPreferencesRefs<T extends Object>(
    Expression<T> Function(
      $$PlayActivitySyncPreferencesTableAnnotationComposer a,
    )
    f,
  ) {
    final $$PlayActivitySyncPreferencesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.instanceId,
          referencedTable: $db.playActivitySyncPreferences,
          getReferencedColumn: (t) => t.serverInstanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PlayActivitySyncPreferencesTableAnnotationComposer(
                $db: $db,
                $table: $db.playActivitySyncPreferences,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> localPlaySessionsRefs<T extends Object>(
    Expression<T> Function($$LocalPlaySessionsTableAnnotationComposer a) f,
  ) {
    final $$LocalPlaySessionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.instanceId,
          referencedTable: $db.localPlaySessions,
          getReferencedColumn: (t) => t.serverInstanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$LocalPlaySessionsTableAnnotationComposer(
                $db: $db,
                $table: $db.localPlaySessions,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> localInstallsRefs<T extends Object>(
    Expression<T> Function($$LocalInstallsTableAnnotationComposer a) f,
  ) {
    final $$LocalInstallsTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.instanceId,
      referencedTable: $db.localInstalls,
      getReferencedColumn: (t) => t.serverInstanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalInstallsTableAnnotationComposer(
            $db: $db,
            $table: $db.localInstalls,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }
}

class $$ServerConnectionsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ServerConnectionsTable,
          ServerConnectionRow,
          $$ServerConnectionsTableFilterComposer,
          $$ServerConnectionsTableOrderingComposer,
          $$ServerConnectionsTableAnnotationComposer,
          $$ServerConnectionsTableCreateCompanionBuilder,
          $$ServerConnectionsTableUpdateCompanionBuilder,
          (ServerConnectionRow, $$ServerConnectionsTableReferences),
          ServerConnectionRow,
          PrefetchHooks Function({
            bool localProfilesRefs,
            bool romdAccountLinksRefs,
            bool profileLocalGamesRefs,
            bool profilePlayHistoriesRefs,
            bool playActivitySyncPreferencesRefs,
            bool localPlaySessionsRefs,
            bool localInstallsRefs,
          })
        > {
  $$ServerConnectionsTableTableManager(
    _$AppDatabase db,
    $ServerConnectionsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ServerConnectionsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$ServerConnectionsTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$ServerConnectionsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> instanceId = const Value.absent(),
                Value<String> lastKnownOrigin = const Value.absent(),
                Value<DateTime> firstSeenAt = const Value.absent(),
                Value<DateTime> lastSeenAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ServerConnectionsCompanion(
                instanceId: instanceId,
                lastKnownOrigin: lastKnownOrigin,
                firstSeenAt: firstSeenAt,
                lastSeenAt: lastSeenAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String instanceId,
                required String lastKnownOrigin,
                required DateTime firstSeenAt,
                required DateTime lastSeenAt,
                Value<int> rowid = const Value.absent(),
              }) => ServerConnectionsCompanion.insert(
                instanceId: instanceId,
                lastKnownOrigin: lastKnownOrigin,
                firstSeenAt: firstSeenAt,
                lastSeenAt: lastSeenAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$ServerConnectionsTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({
                localProfilesRefs = false,
                romdAccountLinksRefs = false,
                profileLocalGamesRefs = false,
                profilePlayHistoriesRefs = false,
                playActivitySyncPreferencesRefs = false,
                localPlaySessionsRefs = false,
                localInstallsRefs = false,
              }) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [
                    if (localProfilesRefs) db.localProfiles,
                    if (romdAccountLinksRefs) db.romdAccountLinks,
                    if (profileLocalGamesRefs) db.profileLocalGames,
                    if (profilePlayHistoriesRefs) db.profilePlayHistories,
                    if (playActivitySyncPreferencesRefs)
                      db.playActivitySyncPreferences,
                    if (localPlaySessionsRefs) db.localPlaySessions,
                    if (localInstallsRefs) db.localInstalls,
                  ],
                  addJoins: null,
                  getPrefetchedDataCallback: (items) async {
                    return [
                      if (localProfilesRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          LocalProfileRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._localProfilesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).localProfilesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) =>
                                    e.selectedServerInstanceId ==
                                    item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (romdAccountLinksRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          RomdAccountLinkRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._romdAccountLinksRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).romdAccountLinksRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (profileLocalGamesRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          ProfileLocalGameRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._profileLocalGamesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).profileLocalGamesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (profilePlayHistoriesRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          ProfilePlayHistoryRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._profilePlayHistoriesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).profilePlayHistoriesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (playActivitySyncPreferencesRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          PlayActivitySyncPreferenceRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._playActivitySyncPreferencesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).playActivitySyncPreferencesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (localPlaySessionsRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          LocalPlaySessionRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._localPlaySessionsRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).localPlaySessionsRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                      if (localInstallsRefs)
                        await $_getPrefetchedData<
                          ServerConnectionRow,
                          $ServerConnectionsTable,
                          LocalInstallRow
                        >(
                          currentTable: table,
                          referencedTable: $$ServerConnectionsTableReferences
                              ._localInstallsRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$ServerConnectionsTableReferences(
                                db,
                                table,
                                p0,
                              ).localInstallsRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.serverInstanceId == item.instanceId,
                              ),
                          typedResults: items,
                        ),
                    ];
                  },
                );
              },
        ),
      );
}

typedef $$ServerConnectionsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ServerConnectionsTable,
      ServerConnectionRow,
      $$ServerConnectionsTableFilterComposer,
      $$ServerConnectionsTableOrderingComposer,
      $$ServerConnectionsTableAnnotationComposer,
      $$ServerConnectionsTableCreateCompanionBuilder,
      $$ServerConnectionsTableUpdateCompanionBuilder,
      (ServerConnectionRow, $$ServerConnectionsTableReferences),
      ServerConnectionRow,
      PrefetchHooks Function({
        bool localProfilesRefs,
        bool romdAccountLinksRefs,
        bool profileLocalGamesRefs,
        bool profilePlayHistoriesRefs,
        bool playActivitySyncPreferencesRefs,
        bool localPlaySessionsRefs,
        bool localInstallsRefs,
      })
    >;
typedef $$LocalProfilesTableCreateCompanionBuilder =
    LocalProfilesCompanion Function({
      required String id,
      required String displayName,
      required String avatarKey,
      required int accentColor,
      Value<String> romdServerOrigin,
      required String entryMode,
      required DateTime createdAt,
      required DateTime updatedAt,
      Value<DateTime?> lastUsedAt,
      Value<String?> selectedServerInstanceId,
      Value<int> serverSelectionGeneration,
      Value<int> rowid,
    });
typedef $$LocalProfilesTableUpdateCompanionBuilder =
    LocalProfilesCompanion Function({
      Value<String> id,
      Value<String> displayName,
      Value<String> avatarKey,
      Value<int> accentColor,
      Value<String> romdServerOrigin,
      Value<String> entryMode,
      Value<DateTime> createdAt,
      Value<DateTime> updatedAt,
      Value<DateTime?> lastUsedAt,
      Value<String?> selectedServerInstanceId,
      Value<int> serverSelectionGeneration,
      Value<int> rowid,
    });

final class $$LocalProfilesTableReferences
    extends
        BaseReferences<_$AppDatabase, $LocalProfilesTable, LocalProfileRow> {
  $$LocalProfilesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $ServerConnectionsTable _selectedServerInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'local_profiles__selected_server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager? get selectedServerInstanceId {
    final $_column = $_itemColumn<String>('selected_server_instance_id');
    if ($_column == null) return null;
    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(
      _selectedServerInstanceIdTable($_db),
    );
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static MultiTypedResultKey<
    $PendingServerLocatorsTable,
    List<PendingServerLocatorRow>
  >
  _pendingServerLocatorsRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.pendingServerLocators,
        aliasName:
            'local_profiles__id__pending_server_locators__local_profile_id',
      );

  $$PendingServerLocatorsTableProcessedTableManager
  get pendingServerLocatorsRefs {
    final manager = $$PendingServerLocatorsTableTableManager(
      $_db,
      $_db.pendingServerLocators,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _pendingServerLocatorsRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$RomdAccountLinksTable, List<RomdAccountLinkRow>>
  _romdAccountLinksRefsTable(_$AppDatabase db) => MultiTypedResultKey.fromTable(
    db.romdAccountLinks,
    aliasName: 'local_profiles__id__romd_account_links__local_profile_id',
  );

  $$RomdAccountLinksTableProcessedTableManager get romdAccountLinksRefs {
    final manager = $$RomdAccountLinksTableTableManager(
      $_db,
      $_db.romdAccountLinks,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _romdAccountLinksRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$ProfileLocalGamesTable, List<ProfileLocalGameRow>>
  _profileLocalGamesRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.profileLocalGames,
        aliasName: 'local_profiles__id__profile_local_games__local_profile_id',
      );

  $$ProfileLocalGamesTableProcessedTableManager get profileLocalGamesRefs {
    final manager = $$ProfileLocalGamesTableTableManager(
      $_db,
      $_db.profileLocalGames,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _profileLocalGamesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $ProfilePlayHistoriesTable,
    List<ProfilePlayHistoryRow>
  >
  _profilePlayHistoriesRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.profilePlayHistories,
        aliasName:
            'local_profiles__id__profile_play_histories__local_profile_id',
      );

  $$ProfilePlayHistoriesTableProcessedTableManager
  get profilePlayHistoriesRefs {
    final manager = $$ProfilePlayHistoriesTableTableManager(
      $_db,
      $_db.profilePlayHistories,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _profilePlayHistoriesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $PlayActivitySyncPreferencesTable,
    List<PlayActivitySyncPreferenceRow>
  >
  _playActivitySyncPreferencesRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.playActivitySyncPreferences,
    aliasName:
        'local_profiles__id__play_activity_sync_preferences__local_profile_id',
  );

  $$PlayActivitySyncPreferencesTableProcessedTableManager
  get playActivitySyncPreferencesRefs {
    final manager = $$PlayActivitySyncPreferencesTableTableManager(
      $_db,
      $_db.playActivitySyncPreferences,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _playActivitySyncPreferencesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<$LocalPlaySessionsTable, List<LocalPlaySessionRow>>
  _localPlaySessionsRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.localPlaySessions,
        aliasName: 'local_profiles__id__local_play_sessions__local_profile_id',
      );

  $$LocalPlaySessionsTableProcessedTableManager get localPlaySessionsRefs {
    final manager = $$LocalPlaySessionsTableTableManager(
      $_db,
      $_db.localPlaySessions,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _localPlaySessionsRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $ControllerMappingProfilesTable,
    List<ControllerMappingProfileRow>
  >
  _controllerMappingProfilesRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.controllerMappingProfiles,
        aliasName:
            'local_profiles__id__controller_mapping_profiles__local_profile_id',
      );

  $$ControllerMappingProfilesTableProcessedTableManager
  get controllerMappingProfilesRefs {
    final manager = $$ControllerMappingProfilesTableTableManager(
      $_db,
      $_db.controllerMappingProfiles,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _controllerMappingProfilesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }

  static MultiTypedResultKey<
    $ControllerProfileBindingRulesTable,
    List<ControllerProfileBindingRuleRow>
  >
  _controllerProfileBindingRulesRefsTable(
    _$AppDatabase db,
  ) => MultiTypedResultKey.fromTable(
    db.controllerProfileBindingRules,
    aliasName:
        'local_profiles__id__controller_profile_binding_rules__local_profile_id',
  );

  $$ControllerProfileBindingRulesTableProcessedTableManager
  get controllerProfileBindingRulesRefs {
    final manager = $$ControllerProfileBindingRulesTableTableManager(
      $_db,
      $_db.controllerProfileBindingRules,
    ).filter((f) => f.localProfileId.id.sqlEquals($_itemColumn<String>('id')!));

    final cache = $_typedResult.readTableOrNull(
      _controllerProfileBindingRulesRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }
}

class $$LocalProfilesTableFilterComposer
    extends Composer<_$AppDatabase, $LocalProfilesTable> {
  $$LocalProfilesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get avatarKey => $composableBuilder(
    column: $table.avatarKey,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get accentColor => $composableBuilder(
    column: $table.accentColor,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get romdServerOrigin => $composableBuilder(
    column: $table.romdServerOrigin,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get entryMode => $composableBuilder(
    column: $table.entryMode,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastUsedAt => $composableBuilder(
    column: $table.lastUsedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get serverSelectionGeneration => $composableBuilder(
    column: $table.serverSelectionGeneration,
    builder: (column) => ColumnFilters(column),
  );

  $$ServerConnectionsTableFilterComposer get selectedServerInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.selectedServerInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  Expression<bool> pendingServerLocatorsRefs(
    Expression<bool> Function($$PendingServerLocatorsTableFilterComposer f) f,
  ) {
    final $$PendingServerLocatorsTableFilterComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.pendingServerLocators,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PendingServerLocatorsTableFilterComposer(
                $db: $db,
                $table: $db.pendingServerLocators,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<bool> romdAccountLinksRefs(
    Expression<bool> Function($$RomdAccountLinksTableFilterComposer f) f,
  ) {
    final $$RomdAccountLinksTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.id,
      referencedTable: $db.romdAccountLinks,
      getReferencedColumn: (t) => t.localProfileId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$RomdAccountLinksTableFilterComposer(
            $db: $db,
            $table: $db.romdAccountLinks,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> profileLocalGamesRefs(
    Expression<bool> Function($$ProfileLocalGamesTableFilterComposer f) f,
  ) {
    final $$ProfileLocalGamesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.id,
      referencedTable: $db.profileLocalGames,
      getReferencedColumn: (t) => t.localProfileId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ProfileLocalGamesTableFilterComposer(
            $db: $db,
            $table: $db.profileLocalGames,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> profilePlayHistoriesRefs(
    Expression<bool> Function($$ProfilePlayHistoriesTableFilterComposer f) f,
  ) {
    final $$ProfilePlayHistoriesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.id,
      referencedTable: $db.profilePlayHistories,
      getReferencedColumn: (t) => t.localProfileId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ProfilePlayHistoriesTableFilterComposer(
            $db: $db,
            $table: $db.profilePlayHistories,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> playActivitySyncPreferencesRefs(
    Expression<bool> Function(
      $$PlayActivitySyncPreferencesTableFilterComposer f,
    )
    f,
  ) {
    final $$PlayActivitySyncPreferencesTableFilterComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.playActivitySyncPreferences,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PlayActivitySyncPreferencesTableFilterComposer(
                $db: $db,
                $table: $db.playActivitySyncPreferences,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<bool> localPlaySessionsRefs(
    Expression<bool> Function($$LocalPlaySessionsTableFilterComposer f) f,
  ) {
    final $$LocalPlaySessionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.id,
      referencedTable: $db.localPlaySessions,
      getReferencedColumn: (t) => t.localProfileId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalPlaySessionsTableFilterComposer(
            $db: $db,
            $table: $db.localPlaySessions,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<bool> controllerMappingProfilesRefs(
    Expression<bool> Function($$ControllerMappingProfilesTableFilterComposer f)
    f,
  ) {
    final $$ControllerMappingProfilesTableFilterComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.controllerMappingProfiles,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ControllerMappingProfilesTableFilterComposer(
                $db: $db,
                $table: $db.controllerMappingProfiles,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<bool> controllerProfileBindingRulesRefs(
    Expression<bool> Function(
      $$ControllerProfileBindingRulesTableFilterComposer f,
    )
    f,
  ) {
    final $$ControllerProfileBindingRulesTableFilterComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.controllerProfileBindingRules,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ControllerProfileBindingRulesTableFilterComposer(
                $db: $db,
                $table: $db.controllerProfileBindingRules,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }
}

class $$LocalProfilesTableOrderingComposer
    extends Composer<_$AppDatabase, $LocalProfilesTable> {
  $$LocalProfilesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get avatarKey => $composableBuilder(
    column: $table.avatarKey,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get accentColor => $composableBuilder(
    column: $table.accentColor,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get romdServerOrigin => $composableBuilder(
    column: $table.romdServerOrigin,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get entryMode => $composableBuilder(
    column: $table.entryMode,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastUsedAt => $composableBuilder(
    column: $table.lastUsedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get serverSelectionGeneration => $composableBuilder(
    column: $table.serverSelectionGeneration,
    builder: (column) => ColumnOrderings(column),
  );

  $$ServerConnectionsTableOrderingComposer get selectedServerInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.selectedServerInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$LocalProfilesTableAnnotationComposer
    extends Composer<_$AppDatabase, $LocalProfilesTable> {
  $$LocalProfilesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get avatarKey =>
      $composableBuilder(column: $table.avatarKey, builder: (column) => column);

  GeneratedColumn<int> get accentColor => $composableBuilder(
    column: $table.accentColor,
    builder: (column) => column,
  );

  GeneratedColumn<String> get romdServerOrigin => $composableBuilder(
    column: $table.romdServerOrigin,
    builder: (column) => column,
  );

  GeneratedColumn<String> get entryMode =>
      $composableBuilder(column: $table.entryMode, builder: (column) => column);

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);

  GeneratedColumn<DateTime> get lastUsedAt => $composableBuilder(
    column: $table.lastUsedAt,
    builder: (column) => column,
  );

  GeneratedColumn<int> get serverSelectionGeneration => $composableBuilder(
    column: $table.serverSelectionGeneration,
    builder: (column) => column,
  );

  $$ServerConnectionsTableAnnotationComposer get selectedServerInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.selectedServerInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }

  Expression<T> pendingServerLocatorsRefs<T extends Object>(
    Expression<T> Function($$PendingServerLocatorsTableAnnotationComposer a) f,
  ) {
    final $$PendingServerLocatorsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.pendingServerLocators,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PendingServerLocatorsTableAnnotationComposer(
                $db: $db,
                $table: $db.pendingServerLocators,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> romdAccountLinksRefs<T extends Object>(
    Expression<T> Function($$RomdAccountLinksTableAnnotationComposer a) f,
  ) {
    final $$RomdAccountLinksTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.id,
      referencedTable: $db.romdAccountLinks,
      getReferencedColumn: (t) => t.localProfileId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$RomdAccountLinksTableAnnotationComposer(
            $db: $db,
            $table: $db.romdAccountLinks,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }

  Expression<T> profileLocalGamesRefs<T extends Object>(
    Expression<T> Function($$ProfileLocalGamesTableAnnotationComposer a) f,
  ) {
    final $$ProfileLocalGamesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.profileLocalGames,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ProfileLocalGamesTableAnnotationComposer(
                $db: $db,
                $table: $db.profileLocalGames,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> profilePlayHistoriesRefs<T extends Object>(
    Expression<T> Function($$ProfilePlayHistoriesTableAnnotationComposer a) f,
  ) {
    final $$ProfilePlayHistoriesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.profilePlayHistories,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ProfilePlayHistoriesTableAnnotationComposer(
                $db: $db,
                $table: $db.profilePlayHistories,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> playActivitySyncPreferencesRefs<T extends Object>(
    Expression<T> Function(
      $$PlayActivitySyncPreferencesTableAnnotationComposer a,
    )
    f,
  ) {
    final $$PlayActivitySyncPreferencesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.playActivitySyncPreferences,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PlayActivitySyncPreferencesTableAnnotationComposer(
                $db: $db,
                $table: $db.playActivitySyncPreferences,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> localPlaySessionsRefs<T extends Object>(
    Expression<T> Function($$LocalPlaySessionsTableAnnotationComposer a) f,
  ) {
    final $$LocalPlaySessionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.localPlaySessions,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$LocalPlaySessionsTableAnnotationComposer(
                $db: $db,
                $table: $db.localPlaySessions,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> controllerMappingProfilesRefs<T extends Object>(
    Expression<T> Function($$ControllerMappingProfilesTableAnnotationComposer a)
    f,
  ) {
    final $$ControllerMappingProfilesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.controllerMappingProfiles,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ControllerMappingProfilesTableAnnotationComposer(
                $db: $db,
                $table: $db.controllerMappingProfiles,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }

  Expression<T> controllerProfileBindingRulesRefs<T extends Object>(
    Expression<T> Function(
      $$ControllerProfileBindingRulesTableAnnotationComposer a,
    )
    f,
  ) {
    final $$ControllerProfileBindingRulesTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.id,
          referencedTable: $db.controllerProfileBindingRules,
          getReferencedColumn: (t) => t.localProfileId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ControllerProfileBindingRulesTableAnnotationComposer(
                $db: $db,
                $table: $db.controllerProfileBindingRules,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }
}

class $$LocalProfilesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $LocalProfilesTable,
          LocalProfileRow,
          $$LocalProfilesTableFilterComposer,
          $$LocalProfilesTableOrderingComposer,
          $$LocalProfilesTableAnnotationComposer,
          $$LocalProfilesTableCreateCompanionBuilder,
          $$LocalProfilesTableUpdateCompanionBuilder,
          (LocalProfileRow, $$LocalProfilesTableReferences),
          LocalProfileRow,
          PrefetchHooks Function({
            bool selectedServerInstanceId,
            bool pendingServerLocatorsRefs,
            bool romdAccountLinksRefs,
            bool profileLocalGamesRefs,
            bool profilePlayHistoriesRefs,
            bool playActivitySyncPreferencesRefs,
            bool localPlaySessionsRefs,
            bool controllerMappingProfilesRefs,
            bool controllerProfileBindingRulesRefs,
          })
        > {
  $$LocalProfilesTableTableManager(_$AppDatabase db, $LocalProfilesTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$LocalProfilesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$LocalProfilesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$LocalProfilesTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> id = const Value.absent(),
                Value<String> displayName = const Value.absent(),
                Value<String> avatarKey = const Value.absent(),
                Value<int> accentColor = const Value.absent(),
                Value<String> romdServerOrigin = const Value.absent(),
                Value<String> entryMode = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<DateTime?> lastUsedAt = const Value.absent(),
                Value<String?> selectedServerInstanceId = const Value.absent(),
                Value<int> serverSelectionGeneration = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LocalProfilesCompanion(
                id: id,
                displayName: displayName,
                avatarKey: avatarKey,
                accentColor: accentColor,
                romdServerOrigin: romdServerOrigin,
                entryMode: entryMode,
                createdAt: createdAt,
                updatedAt: updatedAt,
                lastUsedAt: lastUsedAt,
                selectedServerInstanceId: selectedServerInstanceId,
                serverSelectionGeneration: serverSelectionGeneration,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String id,
                required String displayName,
                required String avatarKey,
                required int accentColor,
                Value<String> romdServerOrigin = const Value.absent(),
                required String entryMode,
                required DateTime createdAt,
                required DateTime updatedAt,
                Value<DateTime?> lastUsedAt = const Value.absent(),
                Value<String?> selectedServerInstanceId = const Value.absent(),
                Value<int> serverSelectionGeneration = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LocalProfilesCompanion.insert(
                id: id,
                displayName: displayName,
                avatarKey: avatarKey,
                accentColor: accentColor,
                romdServerOrigin: romdServerOrigin,
                entryMode: entryMode,
                createdAt: createdAt,
                updatedAt: updatedAt,
                lastUsedAt: lastUsedAt,
                selectedServerInstanceId: selectedServerInstanceId,
                serverSelectionGeneration: serverSelectionGeneration,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$LocalProfilesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({
                selectedServerInstanceId = false,
                pendingServerLocatorsRefs = false,
                romdAccountLinksRefs = false,
                profileLocalGamesRefs = false,
                profilePlayHistoriesRefs = false,
                playActivitySyncPreferencesRefs = false,
                localPlaySessionsRefs = false,
                controllerMappingProfilesRefs = false,
                controllerProfileBindingRulesRefs = false,
              }) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [
                    if (pendingServerLocatorsRefs) db.pendingServerLocators,
                    if (romdAccountLinksRefs) db.romdAccountLinks,
                    if (profileLocalGamesRefs) db.profileLocalGames,
                    if (profilePlayHistoriesRefs) db.profilePlayHistories,
                    if (playActivitySyncPreferencesRefs)
                      db.playActivitySyncPreferences,
                    if (localPlaySessionsRefs) db.localPlaySessions,
                    if (controllerMappingProfilesRefs)
                      db.controllerMappingProfiles,
                    if (controllerProfileBindingRulesRefs)
                      db.controllerProfileBindingRules,
                  ],
                  addJoins:
                      <
                        T extends TableManagerState<
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic
                        >
                      >(state) {
                        if (selectedServerInstanceId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn:
                                        table.selectedServerInstanceId,
                                    referencedTable:
                                        $$LocalProfilesTableReferences
                                            ._selectedServerInstanceIdTable(db),
                                    referencedColumn:
                                        $$LocalProfilesTableReferences
                                            ._selectedServerInstanceIdTable(db)
                                            .instanceId,
                                  )
                                  as T;
                        }

                        return state;
                      },
                  getPrefetchedDataCallback: (items) async {
                    return [
                      if (pendingServerLocatorsRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          PendingServerLocatorRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._pendingServerLocatorsRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).pendingServerLocatorsRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (romdAccountLinksRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          RomdAccountLinkRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._romdAccountLinksRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).romdAccountLinksRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (profileLocalGamesRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          ProfileLocalGameRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._profileLocalGamesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).profileLocalGamesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (profilePlayHistoriesRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          ProfilePlayHistoryRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._profilePlayHistoriesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).profilePlayHistoriesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (playActivitySyncPreferencesRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          PlayActivitySyncPreferenceRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._playActivitySyncPreferencesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).playActivitySyncPreferencesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (localPlaySessionsRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          LocalPlaySessionRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._localPlaySessionsRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).localPlaySessionsRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (controllerMappingProfilesRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          ControllerMappingProfileRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._controllerMappingProfilesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).controllerMappingProfilesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                      if (controllerProfileBindingRulesRefs)
                        await $_getPrefetchedData<
                          LocalProfileRow,
                          $LocalProfilesTable,
                          ControllerProfileBindingRuleRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalProfilesTableReferences
                              ._controllerProfileBindingRulesRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalProfilesTableReferences(
                                db,
                                table,
                                p0,
                              ).controllerProfileBindingRulesRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.localProfileId == item.id,
                              ),
                          typedResults: items,
                        ),
                    ];
                  },
                );
              },
        ),
      );
}

typedef $$LocalProfilesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $LocalProfilesTable,
      LocalProfileRow,
      $$LocalProfilesTableFilterComposer,
      $$LocalProfilesTableOrderingComposer,
      $$LocalProfilesTableAnnotationComposer,
      $$LocalProfilesTableCreateCompanionBuilder,
      $$LocalProfilesTableUpdateCompanionBuilder,
      (LocalProfileRow, $$LocalProfilesTableReferences),
      LocalProfileRow,
      PrefetchHooks Function({
        bool selectedServerInstanceId,
        bool pendingServerLocatorsRefs,
        bool romdAccountLinksRefs,
        bool profileLocalGamesRefs,
        bool profilePlayHistoriesRefs,
        bool playActivitySyncPreferencesRefs,
        bool localPlaySessionsRefs,
        bool controllerMappingProfilesRefs,
        bool controllerProfileBindingRulesRefs,
      })
    >;
typedef $$PendingServerLocatorsTableCreateCompanionBuilder =
    PendingServerLocatorsCompanion Function({
      required String localProfileId,
      required String normalizedOrigin,
      required DateTime createdAt,
      Value<DateTime?> lastAttemptAt,
      Value<int> rowid,
    });
typedef $$PendingServerLocatorsTableUpdateCompanionBuilder =
    PendingServerLocatorsCompanion Function({
      Value<String> localProfileId,
      Value<String> normalizedOrigin,
      Value<DateTime> createdAt,
      Value<DateTime?> lastAttemptAt,
      Value<int> rowid,
    });

final class $$PendingServerLocatorsTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $PendingServerLocatorsTable,
          PendingServerLocatorRow
        > {
  $$PendingServerLocatorsTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) =>
      db.localProfiles.createAlias(
        'pending_server_locators__local_profile_id__local_profiles__id',
      );

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$PendingServerLocatorsTableFilterComposer
    extends Composer<_$AppDatabase, $PendingServerLocatorsTable> {
  $$PendingServerLocatorsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get normalizedOrigin => $composableBuilder(
    column: $table.normalizedOrigin,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastAttemptAt => $composableBuilder(
    column: $table.lastAttemptAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PendingServerLocatorsTableOrderingComposer
    extends Composer<_$AppDatabase, $PendingServerLocatorsTable> {
  $$PendingServerLocatorsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get normalizedOrigin => $composableBuilder(
    column: $table.normalizedOrigin,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastAttemptAt => $composableBuilder(
    column: $table.lastAttemptAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PendingServerLocatorsTableAnnotationComposer
    extends Composer<_$AppDatabase, $PendingServerLocatorsTable> {
  $$PendingServerLocatorsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get normalizedOrigin => $composableBuilder(
    column: $table.normalizedOrigin,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<DateTime> get lastAttemptAt => $composableBuilder(
    column: $table.lastAttemptAt,
    builder: (column) => column,
  );

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PendingServerLocatorsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $PendingServerLocatorsTable,
          PendingServerLocatorRow,
          $$PendingServerLocatorsTableFilterComposer,
          $$PendingServerLocatorsTableOrderingComposer,
          $$PendingServerLocatorsTableAnnotationComposer,
          $$PendingServerLocatorsTableCreateCompanionBuilder,
          $$PendingServerLocatorsTableUpdateCompanionBuilder,
          (PendingServerLocatorRow, $$PendingServerLocatorsTableReferences),
          PendingServerLocatorRow,
          PrefetchHooks Function({bool localProfileId})
        > {
  $$PendingServerLocatorsTableTableManager(
    _$AppDatabase db,
    $PendingServerLocatorsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$PendingServerLocatorsTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$PendingServerLocatorsTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$PendingServerLocatorsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> normalizedOrigin = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<DateTime?> lastAttemptAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => PendingServerLocatorsCompanion(
                localProfileId: localProfileId,
                normalizedOrigin: normalizedOrigin,
                createdAt: createdAt,
                lastAttemptAt: lastAttemptAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String normalizedOrigin,
                required DateTime createdAt,
                Value<DateTime?> lastAttemptAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => PendingServerLocatorsCompanion.insert(
                localProfileId: localProfileId,
                normalizedOrigin: normalizedOrigin,
                createdAt: createdAt,
                lastAttemptAt: lastAttemptAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$PendingServerLocatorsTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({localProfileId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (localProfileId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.localProfileId,
                                referencedTable:
                                    $$PendingServerLocatorsTableReferences
                                        ._localProfileIdTable(db),
                                referencedColumn:
                                    $$PendingServerLocatorsTableReferences
                                        ._localProfileIdTable(db)
                                        .id,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$PendingServerLocatorsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $PendingServerLocatorsTable,
      PendingServerLocatorRow,
      $$PendingServerLocatorsTableFilterComposer,
      $$PendingServerLocatorsTableOrderingComposer,
      $$PendingServerLocatorsTableAnnotationComposer,
      $$PendingServerLocatorsTableCreateCompanionBuilder,
      $$PendingServerLocatorsTableUpdateCompanionBuilder,
      (PendingServerLocatorRow, $$PendingServerLocatorsTableReferences),
      PendingServerLocatorRow,
      PrefetchHooks Function({bool localProfileId})
    >;
typedef $$RomdAccountLinksTableCreateCompanionBuilder =
    RomdAccountLinksCompanion Function({
      required String localProfileId,
      required String romdUserId,
      required String username,
      required String email,
      required DateTime linkedAt,
      Value<DateTime?> lastLoginAt,
      Value<String?> serverInstanceId,
      Value<int> rowid,
    });
typedef $$RomdAccountLinksTableUpdateCompanionBuilder =
    RomdAccountLinksCompanion Function({
      Value<String> localProfileId,
      Value<String> romdUserId,
      Value<String> username,
      Value<String> email,
      Value<DateTime> linkedAt,
      Value<DateTime?> lastLoginAt,
      Value<String?> serverInstanceId,
      Value<int> rowid,
    });

final class $$RomdAccountLinksTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $RomdAccountLinksTable,
          RomdAccountLinkRow
        > {
  $$RomdAccountLinksTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) => db
      .localProfiles
      .createAlias('romd_account_links__local_profile_id__local_profiles__id');

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static $ServerConnectionsTable _serverInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'romd_account_links__server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager? get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id');
    if ($_column == null) return null;
    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$RomdAccountLinksTableFilterComposer
    extends Composer<_$AppDatabase, $RomdAccountLinksTable> {
  $$RomdAccountLinksTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get romdUserId => $composableBuilder(
    column: $table.romdUserId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get username => $composableBuilder(
    column: $table.username,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get email => $composableBuilder(
    column: $table.email,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get linkedAt => $composableBuilder(
    column: $table.linkedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastLoginAt => $composableBuilder(
    column: $table.lastLoginAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$RomdAccountLinksTableOrderingComposer
    extends Composer<_$AppDatabase, $RomdAccountLinksTable> {
  $$RomdAccountLinksTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get romdUserId => $composableBuilder(
    column: $table.romdUserId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get username => $composableBuilder(
    column: $table.username,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get email => $composableBuilder(
    column: $table.email,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get linkedAt => $composableBuilder(
    column: $table.linkedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastLoginAt => $composableBuilder(
    column: $table.lastLoginAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$RomdAccountLinksTableAnnotationComposer
    extends Composer<_$AppDatabase, $RomdAccountLinksTable> {
  $$RomdAccountLinksTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get romdUserId => $composableBuilder(
    column: $table.romdUserId,
    builder: (column) => column,
  );

  GeneratedColumn<String> get username =>
      $composableBuilder(column: $table.username, builder: (column) => column);

  GeneratedColumn<String> get email =>
      $composableBuilder(column: $table.email, builder: (column) => column);

  GeneratedColumn<DateTime> get linkedAt =>
      $composableBuilder(column: $table.linkedAt, builder: (column) => column);

  GeneratedColumn<DateTime> get lastLoginAt => $composableBuilder(
    column: $table.lastLoginAt,
    builder: (column) => column,
  );

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$RomdAccountLinksTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $RomdAccountLinksTable,
          RomdAccountLinkRow,
          $$RomdAccountLinksTableFilterComposer,
          $$RomdAccountLinksTableOrderingComposer,
          $$RomdAccountLinksTableAnnotationComposer,
          $$RomdAccountLinksTableCreateCompanionBuilder,
          $$RomdAccountLinksTableUpdateCompanionBuilder,
          (RomdAccountLinkRow, $$RomdAccountLinksTableReferences),
          RomdAccountLinkRow,
          PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
        > {
  $$RomdAccountLinksTableTableManager(
    _$AppDatabase db,
    $RomdAccountLinksTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$RomdAccountLinksTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$RomdAccountLinksTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$RomdAccountLinksTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> romdUserId = const Value.absent(),
                Value<String> username = const Value.absent(),
                Value<String> email = const Value.absent(),
                Value<DateTime> linkedAt = const Value.absent(),
                Value<DateTime?> lastLoginAt = const Value.absent(),
                Value<String?> serverInstanceId = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => RomdAccountLinksCompanion(
                localProfileId: localProfileId,
                romdUserId: romdUserId,
                username: username,
                email: email,
                linkedAt: linkedAt,
                lastLoginAt: lastLoginAt,
                serverInstanceId: serverInstanceId,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String romdUserId,
                required String username,
                required String email,
                required DateTime linkedAt,
                Value<DateTime?> lastLoginAt = const Value.absent(),
                Value<String?> serverInstanceId = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => RomdAccountLinksCompanion.insert(
                localProfileId: localProfileId,
                romdUserId: romdUserId,
                username: username,
                email: email,
                linkedAt: linkedAt,
                lastLoginAt: lastLoginAt,
                serverInstanceId: serverInstanceId,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$RomdAccountLinksTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({localProfileId = false, serverInstanceId = false}) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [],
                  addJoins:
                      <
                        T extends TableManagerState<
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic
                        >
                      >(state) {
                        if (localProfileId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.localProfileId,
                                    referencedTable:
                                        $$RomdAccountLinksTableReferences
                                            ._localProfileIdTable(db),
                                    referencedColumn:
                                        $$RomdAccountLinksTableReferences
                                            ._localProfileIdTable(db)
                                            .id,
                                  )
                                  as T;
                        }
                        if (serverInstanceId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.serverInstanceId,
                                    referencedTable:
                                        $$RomdAccountLinksTableReferences
                                            ._serverInstanceIdTable(db),
                                    referencedColumn:
                                        $$RomdAccountLinksTableReferences
                                            ._serverInstanceIdTable(db)
                                            .instanceId,
                                  )
                                  as T;
                        }

                        return state;
                      },
                  getPrefetchedDataCallback: (items) async {
                    return [];
                  },
                );
              },
        ),
      );
}

typedef $$RomdAccountLinksTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $RomdAccountLinksTable,
      RomdAccountLinkRow,
      $$RomdAccountLinksTableFilterComposer,
      $$RomdAccountLinksTableOrderingComposer,
      $$RomdAccountLinksTableAnnotationComposer,
      $$RomdAccountLinksTableCreateCompanionBuilder,
      $$RomdAccountLinksTableUpdateCompanionBuilder,
      (RomdAccountLinkRow, $$RomdAccountLinksTableReferences),
      RomdAccountLinkRow,
      PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
    >;
typedef $$ProfileLocalGamesTableCreateCompanionBuilder =
    ProfileLocalGamesCompanion Function({
      required String localProfileId,
      required String serverInstanceId,
      required String releaseId,
      required String titleId,
      required String authorizationState,
      required DateTime acquiredAt,
      required DateTime lastCheckedAt,
      Value<int> rowid,
    });
typedef $$ProfileLocalGamesTableUpdateCompanionBuilder =
    ProfileLocalGamesCompanion Function({
      Value<String> localProfileId,
      Value<String> serverInstanceId,
      Value<String> releaseId,
      Value<String> titleId,
      Value<String> authorizationState,
      Value<DateTime> acquiredAt,
      Value<DateTime> lastCheckedAt,
      Value<int> rowid,
    });

final class $$ProfileLocalGamesTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $ProfileLocalGamesTable,
          ProfileLocalGameRow
        > {
  $$ProfileLocalGamesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) => db
      .localProfiles
      .createAlias('profile_local_games__local_profile_id__local_profiles__id');

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static $ServerConnectionsTable _serverInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'profile_local_games__server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id')!;

    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$ProfileLocalGamesTableFilterComposer
    extends Composer<_$AppDatabase, $ProfileLocalGamesTable> {
  $$ProfileLocalGamesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get authorizationState => $composableBuilder(
    column: $table.authorizationState,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get acquiredAt => $composableBuilder(
    column: $table.acquiredAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastCheckedAt => $composableBuilder(
    column: $table.lastCheckedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ProfileLocalGamesTableOrderingComposer
    extends Composer<_$AppDatabase, $ProfileLocalGamesTable> {
  $$ProfileLocalGamesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get authorizationState => $composableBuilder(
    column: $table.authorizationState,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get acquiredAt => $composableBuilder(
    column: $table.acquiredAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastCheckedAt => $composableBuilder(
    column: $table.lastCheckedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ProfileLocalGamesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ProfileLocalGamesTable> {
  $$ProfileLocalGamesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get releaseId =>
      $composableBuilder(column: $table.releaseId, builder: (column) => column);

  GeneratedColumn<String> get titleId =>
      $composableBuilder(column: $table.titleId, builder: (column) => column);

  GeneratedColumn<String> get authorizationState => $composableBuilder(
    column: $table.authorizationState,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get acquiredAt => $composableBuilder(
    column: $table.acquiredAt,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get lastCheckedAt => $composableBuilder(
    column: $table.lastCheckedAt,
    builder: (column) => column,
  );

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$ProfileLocalGamesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ProfileLocalGamesTable,
          ProfileLocalGameRow,
          $$ProfileLocalGamesTableFilterComposer,
          $$ProfileLocalGamesTableOrderingComposer,
          $$ProfileLocalGamesTableAnnotationComposer,
          $$ProfileLocalGamesTableCreateCompanionBuilder,
          $$ProfileLocalGamesTableUpdateCompanionBuilder,
          (ProfileLocalGameRow, $$ProfileLocalGamesTableReferences),
          ProfileLocalGameRow,
          PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
        > {
  $$ProfileLocalGamesTableTableManager(
    _$AppDatabase db,
    $ProfileLocalGamesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ProfileLocalGamesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$ProfileLocalGamesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$ProfileLocalGamesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> serverInstanceId = const Value.absent(),
                Value<String> releaseId = const Value.absent(),
                Value<String> titleId = const Value.absent(),
                Value<String> authorizationState = const Value.absent(),
                Value<DateTime> acquiredAt = const Value.absent(),
                Value<DateTime> lastCheckedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ProfileLocalGamesCompanion(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                releaseId: releaseId,
                titleId: titleId,
                authorizationState: authorizationState,
                acquiredAt: acquiredAt,
                lastCheckedAt: lastCheckedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String serverInstanceId,
                required String releaseId,
                required String titleId,
                required String authorizationState,
                required DateTime acquiredAt,
                required DateTime lastCheckedAt,
                Value<int> rowid = const Value.absent(),
              }) => ProfileLocalGamesCompanion.insert(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                releaseId: releaseId,
                titleId: titleId,
                authorizationState: authorizationState,
                acquiredAt: acquiredAt,
                lastCheckedAt: lastCheckedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$ProfileLocalGamesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({localProfileId = false, serverInstanceId = false}) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [],
                  addJoins:
                      <
                        T extends TableManagerState<
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic
                        >
                      >(state) {
                        if (localProfileId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.localProfileId,
                                    referencedTable:
                                        $$ProfileLocalGamesTableReferences
                                            ._localProfileIdTable(db),
                                    referencedColumn:
                                        $$ProfileLocalGamesTableReferences
                                            ._localProfileIdTable(db)
                                            .id,
                                  )
                                  as T;
                        }
                        if (serverInstanceId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.serverInstanceId,
                                    referencedTable:
                                        $$ProfileLocalGamesTableReferences
                                            ._serverInstanceIdTable(db),
                                    referencedColumn:
                                        $$ProfileLocalGamesTableReferences
                                            ._serverInstanceIdTable(db)
                                            .instanceId,
                                  )
                                  as T;
                        }

                        return state;
                      },
                  getPrefetchedDataCallback: (items) async {
                    return [];
                  },
                );
              },
        ),
      );
}

typedef $$ProfileLocalGamesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ProfileLocalGamesTable,
      ProfileLocalGameRow,
      $$ProfileLocalGamesTableFilterComposer,
      $$ProfileLocalGamesTableOrderingComposer,
      $$ProfileLocalGamesTableAnnotationComposer,
      $$ProfileLocalGamesTableCreateCompanionBuilder,
      $$ProfileLocalGamesTableUpdateCompanionBuilder,
      (ProfileLocalGameRow, $$ProfileLocalGamesTableReferences),
      ProfileLocalGameRow,
      PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
    >;
typedef $$ProfilePlayHistoriesTableCreateCompanionBuilder =
    ProfilePlayHistoriesCompanion Function({
      required String localProfileId,
      required String serverInstanceId,
      required String titleId,
      required String lastReleaseId,
      required DateTime lastPlayedAt,
      required int playCount,
      Value<int> rowid,
    });
typedef $$ProfilePlayHistoriesTableUpdateCompanionBuilder =
    ProfilePlayHistoriesCompanion Function({
      Value<String> localProfileId,
      Value<String> serverInstanceId,
      Value<String> titleId,
      Value<String> lastReleaseId,
      Value<DateTime> lastPlayedAt,
      Value<int> playCount,
      Value<int> rowid,
    });

final class $$ProfilePlayHistoriesTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $ProfilePlayHistoriesTable,
          ProfilePlayHistoryRow
        > {
  $$ProfilePlayHistoriesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) =>
      db.localProfiles.createAlias(
        'profile_play_histories__local_profile_id__local_profiles__id',
      );

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static $ServerConnectionsTable _serverInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'profile_play_histories__server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id')!;

    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$ProfilePlayHistoriesTableFilterComposer
    extends Composer<_$AppDatabase, $ProfilePlayHistoriesTable> {
  $$ProfilePlayHistoriesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get lastReleaseId => $composableBuilder(
    column: $table.lastReleaseId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get playCount => $composableBuilder(
    column: $table.playCount,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ProfilePlayHistoriesTableOrderingComposer
    extends Composer<_$AppDatabase, $ProfilePlayHistoriesTable> {
  $$ProfilePlayHistoriesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get lastReleaseId => $composableBuilder(
    column: $table.lastReleaseId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get playCount => $composableBuilder(
    column: $table.playCount,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ProfilePlayHistoriesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ProfilePlayHistoriesTable> {
  $$ProfilePlayHistoriesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get titleId =>
      $composableBuilder(column: $table.titleId, builder: (column) => column);

  GeneratedColumn<String> get lastReleaseId => $composableBuilder(
    column: $table.lastReleaseId,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => column,
  );

  GeneratedColumn<int> get playCount =>
      $composableBuilder(column: $table.playCount, builder: (column) => column);

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$ProfilePlayHistoriesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ProfilePlayHistoriesTable,
          ProfilePlayHistoryRow,
          $$ProfilePlayHistoriesTableFilterComposer,
          $$ProfilePlayHistoriesTableOrderingComposer,
          $$ProfilePlayHistoriesTableAnnotationComposer,
          $$ProfilePlayHistoriesTableCreateCompanionBuilder,
          $$ProfilePlayHistoriesTableUpdateCompanionBuilder,
          (ProfilePlayHistoryRow, $$ProfilePlayHistoriesTableReferences),
          ProfilePlayHistoryRow,
          PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
        > {
  $$ProfilePlayHistoriesTableTableManager(
    _$AppDatabase db,
    $ProfilePlayHistoriesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ProfilePlayHistoriesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$ProfilePlayHistoriesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ProfilePlayHistoriesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> serverInstanceId = const Value.absent(),
                Value<String> titleId = const Value.absent(),
                Value<String> lastReleaseId = const Value.absent(),
                Value<DateTime> lastPlayedAt = const Value.absent(),
                Value<int> playCount = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ProfilePlayHistoriesCompanion(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                titleId: titleId,
                lastReleaseId: lastReleaseId,
                lastPlayedAt: lastPlayedAt,
                playCount: playCount,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String serverInstanceId,
                required String titleId,
                required String lastReleaseId,
                required DateTime lastPlayedAt,
                required int playCount,
                Value<int> rowid = const Value.absent(),
              }) => ProfilePlayHistoriesCompanion.insert(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                titleId: titleId,
                lastReleaseId: lastReleaseId,
                lastPlayedAt: lastPlayedAt,
                playCount: playCount,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$ProfilePlayHistoriesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({localProfileId = false, serverInstanceId = false}) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [],
                  addJoins:
                      <
                        T extends TableManagerState<
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic
                        >
                      >(state) {
                        if (localProfileId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.localProfileId,
                                    referencedTable:
                                        $$ProfilePlayHistoriesTableReferences
                                            ._localProfileIdTable(db),
                                    referencedColumn:
                                        $$ProfilePlayHistoriesTableReferences
                                            ._localProfileIdTable(db)
                                            .id,
                                  )
                                  as T;
                        }
                        if (serverInstanceId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.serverInstanceId,
                                    referencedTable:
                                        $$ProfilePlayHistoriesTableReferences
                                            ._serverInstanceIdTable(db),
                                    referencedColumn:
                                        $$ProfilePlayHistoriesTableReferences
                                            ._serverInstanceIdTable(db)
                                            .instanceId,
                                  )
                                  as T;
                        }

                        return state;
                      },
                  getPrefetchedDataCallback: (items) async {
                    return [];
                  },
                );
              },
        ),
      );
}

typedef $$ProfilePlayHistoriesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ProfilePlayHistoriesTable,
      ProfilePlayHistoryRow,
      $$ProfilePlayHistoriesTableFilterComposer,
      $$ProfilePlayHistoriesTableOrderingComposer,
      $$ProfilePlayHistoriesTableAnnotationComposer,
      $$ProfilePlayHistoriesTableCreateCompanionBuilder,
      $$ProfilePlayHistoriesTableUpdateCompanionBuilder,
      (ProfilePlayHistoryRow, $$ProfilePlayHistoriesTableReferences),
      ProfilePlayHistoryRow,
      PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
    >;
typedef $$PlayActivitySyncPreferencesTableCreateCompanionBuilder =
    PlayActivitySyncPreferencesCompanion Function({
      required String localProfileId,
      required String serverInstanceId,
      Value<bool> enabled,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$PlayActivitySyncPreferencesTableUpdateCompanionBuilder =
    PlayActivitySyncPreferencesCompanion Function({
      Value<String> localProfileId,
      Value<String> serverInstanceId,
      Value<bool> enabled,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

final class $$PlayActivitySyncPreferencesTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $PlayActivitySyncPreferencesTable,
          PlayActivitySyncPreferenceRow
        > {
  $$PlayActivitySyncPreferencesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) =>
      db.localProfiles.createAlias(
        'play_activity_sync_preferences__local_profile_id__local_profiles__id',
      );

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static $ServerConnectionsTable _serverInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'play_activity_sync_preferences__server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id')!;

    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$PlayActivitySyncPreferencesTableFilterComposer
    extends Composer<_$AppDatabase, $PlayActivitySyncPreferencesTable> {
  $$PlayActivitySyncPreferencesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<bool> get enabled => $composableBuilder(
    column: $table.enabled,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PlayActivitySyncPreferencesTableOrderingComposer
    extends Composer<_$AppDatabase, $PlayActivitySyncPreferencesTable> {
  $$PlayActivitySyncPreferencesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<bool> get enabled => $composableBuilder(
    column: $table.enabled,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PlayActivitySyncPreferencesTableAnnotationComposer
    extends Composer<_$AppDatabase, $PlayActivitySyncPreferencesTable> {
  $$PlayActivitySyncPreferencesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<bool> get enabled =>
      $composableBuilder(column: $table.enabled, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$PlayActivitySyncPreferencesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $PlayActivitySyncPreferencesTable,
          PlayActivitySyncPreferenceRow,
          $$PlayActivitySyncPreferencesTableFilterComposer,
          $$PlayActivitySyncPreferencesTableOrderingComposer,
          $$PlayActivitySyncPreferencesTableAnnotationComposer,
          $$PlayActivitySyncPreferencesTableCreateCompanionBuilder,
          $$PlayActivitySyncPreferencesTableUpdateCompanionBuilder,
          (
            PlayActivitySyncPreferenceRow,
            $$PlayActivitySyncPreferencesTableReferences,
          ),
          PlayActivitySyncPreferenceRow,
          PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
        > {
  $$PlayActivitySyncPreferencesTableTableManager(
    _$AppDatabase db,
    $PlayActivitySyncPreferencesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$PlayActivitySyncPreferencesTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$PlayActivitySyncPreferencesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$PlayActivitySyncPreferencesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> serverInstanceId = const Value.absent(),
                Value<bool> enabled = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => PlayActivitySyncPreferencesCompanion(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                enabled: enabled,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String serverInstanceId,
                Value<bool> enabled = const Value.absent(),
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => PlayActivitySyncPreferencesCompanion.insert(
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                enabled: enabled,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$PlayActivitySyncPreferencesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({localProfileId = false, serverInstanceId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (localProfileId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.localProfileId,
                                referencedTable:
                                    $$PlayActivitySyncPreferencesTableReferences
                                        ._localProfileIdTable(db),
                                referencedColumn:
                                    $$PlayActivitySyncPreferencesTableReferences
                                        ._localProfileIdTable(db)
                                        .id,
                              )
                              as T;
                    }
                    if (serverInstanceId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.serverInstanceId,
                                referencedTable:
                                    $$PlayActivitySyncPreferencesTableReferences
                                        ._serverInstanceIdTable(db),
                                referencedColumn:
                                    $$PlayActivitySyncPreferencesTableReferences
                                        ._serverInstanceIdTable(db)
                                        .instanceId,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$PlayActivitySyncPreferencesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $PlayActivitySyncPreferencesTable,
      PlayActivitySyncPreferenceRow,
      $$PlayActivitySyncPreferencesTableFilterComposer,
      $$PlayActivitySyncPreferencesTableOrderingComposer,
      $$PlayActivitySyncPreferencesTableAnnotationComposer,
      $$PlayActivitySyncPreferencesTableCreateCompanionBuilder,
      $$PlayActivitySyncPreferencesTableUpdateCompanionBuilder,
      (
        PlayActivitySyncPreferenceRow,
        $$PlayActivitySyncPreferencesTableReferences,
      ),
      PlayActivitySyncPreferenceRow,
      PrefetchHooks Function({bool localProfileId, bool serverInstanceId})
    >;
typedef $$LocalPlaySessionsTableCreateCompanionBuilder =
    LocalPlaySessionsCompanion Function({
      required String sessionId,
      required String localProfileId,
      required String serverInstanceId,
      required String clientId,
      required String titleId,
      required String releaseId,
      required DateTime startedAt,
      Value<DateTime?> endedAt,
      Value<int?> activeDurationSeconds,
      Value<bool> syncEligible,
      required DateTime createdAt,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$LocalPlaySessionsTableUpdateCompanionBuilder =
    LocalPlaySessionsCompanion Function({
      Value<String> sessionId,
      Value<String> localProfileId,
      Value<String> serverInstanceId,
      Value<String> clientId,
      Value<String> titleId,
      Value<String> releaseId,
      Value<DateTime> startedAt,
      Value<DateTime?> endedAt,
      Value<int?> activeDurationSeconds,
      Value<bool> syncEligible,
      Value<DateTime> createdAt,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

final class $$LocalPlaySessionsTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $LocalPlaySessionsTable,
          LocalPlaySessionRow
        > {
  $$LocalPlaySessionsTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) => db
      .localProfiles
      .createAlias('local_play_sessions__local_profile_id__local_profiles__id');

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static $ServerConnectionsTable _serverInstanceIdTable(
    _$AppDatabase db,
  ) => db.serverConnections.createAlias(
    'local_play_sessions__server_instance_id__server_connections__instance_id',
  );

  $$ServerConnectionsTableProcessedTableManager get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id')!;

    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }

  static MultiTypedResultKey<
    $PlayActivityOutboxTable,
    List<PlayActivityOutboxRow>
  >
  _playActivityOutboxRefsTable(_$AppDatabase db) =>
      MultiTypedResultKey.fromTable(
        db.playActivityOutbox,
        aliasName:
            'local_play_sessions__session_id__play_activity_outbox__session_id',
      );

  $$PlayActivityOutboxTableProcessedTableManager get playActivityOutboxRefs {
    final manager =
        $$PlayActivityOutboxTableTableManager(
          $_db,
          $_db.playActivityOutbox,
        ).filter(
          (f) => f.sessionId.sessionId.sqlEquals(
            $_itemColumn<String>('session_id')!,
          ),
        );

    final cache = $_typedResult.readTableOrNull(
      _playActivityOutboxRefsTable($_db),
    );
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: cache),
    );
  }
}

class $$LocalPlaySessionsTableFilterComposer
    extends Composer<_$AppDatabase, $LocalPlaySessionsTable> {
  $$LocalPlaySessionsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get sessionId => $composableBuilder(
    column: $table.sessionId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get clientId => $composableBuilder(
    column: $table.clientId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get startedAt => $composableBuilder(
    column: $table.startedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get endedAt => $composableBuilder(
    column: $table.endedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get activeDurationSeconds => $composableBuilder(
    column: $table.activeDurationSeconds,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<bool> get syncEligible => $composableBuilder(
    column: $table.syncEligible,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  Expression<bool> playActivityOutboxRefs(
    Expression<bool> Function($$PlayActivityOutboxTableFilterComposer f) f,
  ) {
    final $$PlayActivityOutboxTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.sessionId,
      referencedTable: $db.playActivityOutbox,
      getReferencedColumn: (t) => t.sessionId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$PlayActivityOutboxTableFilterComposer(
            $db: $db,
            $table: $db.playActivityOutbox,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return f(composer);
  }
}

class $$LocalPlaySessionsTableOrderingComposer
    extends Composer<_$AppDatabase, $LocalPlaySessionsTable> {
  $$LocalPlaySessionsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get sessionId => $composableBuilder(
    column: $table.sessionId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get clientId => $composableBuilder(
    column: $table.clientId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get startedAt => $composableBuilder(
    column: $table.startedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get endedAt => $composableBuilder(
    column: $table.endedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get activeDurationSeconds => $composableBuilder(
    column: $table.activeDurationSeconds,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<bool> get syncEligible => $composableBuilder(
    column: $table.syncEligible,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$LocalPlaySessionsTableAnnotationComposer
    extends Composer<_$AppDatabase, $LocalPlaySessionsTable> {
  $$LocalPlaySessionsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get sessionId =>
      $composableBuilder(column: $table.sessionId, builder: (column) => column);

  GeneratedColumn<String> get clientId =>
      $composableBuilder(column: $table.clientId, builder: (column) => column);

  GeneratedColumn<String> get titleId =>
      $composableBuilder(column: $table.titleId, builder: (column) => column);

  GeneratedColumn<String> get releaseId =>
      $composableBuilder(column: $table.releaseId, builder: (column) => column);

  GeneratedColumn<DateTime> get startedAt =>
      $composableBuilder(column: $table.startedAt, builder: (column) => column);

  GeneratedColumn<DateTime> get endedAt =>
      $composableBuilder(column: $table.endedAt, builder: (column) => column);

  GeneratedColumn<int> get activeDurationSeconds => $composableBuilder(
    column: $table.activeDurationSeconds,
    builder: (column) => column,
  );

  GeneratedColumn<bool> get syncEligible => $composableBuilder(
    column: $table.syncEligible,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }

  Expression<T> playActivityOutboxRefs<T extends Object>(
    Expression<T> Function($$PlayActivityOutboxTableAnnotationComposer a) f,
  ) {
    final $$PlayActivityOutboxTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.sessionId,
          referencedTable: $db.playActivityOutbox,
          getReferencedColumn: (t) => t.sessionId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$PlayActivityOutboxTableAnnotationComposer(
                $db: $db,
                $table: $db.playActivityOutbox,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return f(composer);
  }
}

class $$LocalPlaySessionsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $LocalPlaySessionsTable,
          LocalPlaySessionRow,
          $$LocalPlaySessionsTableFilterComposer,
          $$LocalPlaySessionsTableOrderingComposer,
          $$LocalPlaySessionsTableAnnotationComposer,
          $$LocalPlaySessionsTableCreateCompanionBuilder,
          $$LocalPlaySessionsTableUpdateCompanionBuilder,
          (LocalPlaySessionRow, $$LocalPlaySessionsTableReferences),
          LocalPlaySessionRow,
          PrefetchHooks Function({
            bool localProfileId,
            bool serverInstanceId,
            bool playActivityOutboxRefs,
          })
        > {
  $$LocalPlaySessionsTableTableManager(
    _$AppDatabase db,
    $LocalPlaySessionsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$LocalPlaySessionsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$LocalPlaySessionsTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$LocalPlaySessionsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> sessionId = const Value.absent(),
                Value<String> localProfileId = const Value.absent(),
                Value<String> serverInstanceId = const Value.absent(),
                Value<String> clientId = const Value.absent(),
                Value<String> titleId = const Value.absent(),
                Value<String> releaseId = const Value.absent(),
                Value<DateTime> startedAt = const Value.absent(),
                Value<DateTime?> endedAt = const Value.absent(),
                Value<int?> activeDurationSeconds = const Value.absent(),
                Value<bool> syncEligible = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LocalPlaySessionsCompanion(
                sessionId: sessionId,
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                clientId: clientId,
                titleId: titleId,
                releaseId: releaseId,
                startedAt: startedAt,
                endedAt: endedAt,
                activeDurationSeconds: activeDurationSeconds,
                syncEligible: syncEligible,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String sessionId,
                required String localProfileId,
                required String serverInstanceId,
                required String clientId,
                required String titleId,
                required String releaseId,
                required DateTime startedAt,
                Value<DateTime?> endedAt = const Value.absent(),
                Value<int?> activeDurationSeconds = const Value.absent(),
                Value<bool> syncEligible = const Value.absent(),
                required DateTime createdAt,
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => LocalPlaySessionsCompanion.insert(
                sessionId: sessionId,
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                clientId: clientId,
                titleId: titleId,
                releaseId: releaseId,
                startedAt: startedAt,
                endedAt: endedAt,
                activeDurationSeconds: activeDurationSeconds,
                syncEligible: syncEligible,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$LocalPlaySessionsTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback:
              ({
                localProfileId = false,
                serverInstanceId = false,
                playActivityOutboxRefs = false,
              }) {
                return PrefetchHooks(
                  db: db,
                  explicitlyWatchedTables: [
                    if (playActivityOutboxRefs) db.playActivityOutbox,
                  ],
                  addJoins:
                      <
                        T extends TableManagerState<
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic,
                          dynamic
                        >
                      >(state) {
                        if (localProfileId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.localProfileId,
                                    referencedTable:
                                        $$LocalPlaySessionsTableReferences
                                            ._localProfileIdTable(db),
                                    referencedColumn:
                                        $$LocalPlaySessionsTableReferences
                                            ._localProfileIdTable(db)
                                            .id,
                                  )
                                  as T;
                        }
                        if (serverInstanceId) {
                          state =
                              state.withJoin(
                                    currentTable: table,
                                    currentColumn: table.serverInstanceId,
                                    referencedTable:
                                        $$LocalPlaySessionsTableReferences
                                            ._serverInstanceIdTable(db),
                                    referencedColumn:
                                        $$LocalPlaySessionsTableReferences
                                            ._serverInstanceIdTable(db)
                                            .instanceId,
                                  )
                                  as T;
                        }

                        return state;
                      },
                  getPrefetchedDataCallback: (items) async {
                    return [
                      if (playActivityOutboxRefs)
                        await $_getPrefetchedData<
                          LocalPlaySessionRow,
                          $LocalPlaySessionsTable,
                          PlayActivityOutboxRow
                        >(
                          currentTable: table,
                          referencedTable: $$LocalPlaySessionsTableReferences
                              ._playActivityOutboxRefsTable(db),
                          managerFromTypedResult: (p0) =>
                              $$LocalPlaySessionsTableReferences(
                                db,
                                table,
                                p0,
                              ).playActivityOutboxRefs,
                          referencedItemsForCurrentItem:
                              (item, referencedItems) => referencedItems.where(
                                (e) => e.sessionId == item.sessionId,
                              ),
                          typedResults: items,
                        ),
                    ];
                  },
                );
              },
        ),
      );
}

typedef $$LocalPlaySessionsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $LocalPlaySessionsTable,
      LocalPlaySessionRow,
      $$LocalPlaySessionsTableFilterComposer,
      $$LocalPlaySessionsTableOrderingComposer,
      $$LocalPlaySessionsTableAnnotationComposer,
      $$LocalPlaySessionsTableCreateCompanionBuilder,
      $$LocalPlaySessionsTableUpdateCompanionBuilder,
      (LocalPlaySessionRow, $$LocalPlaySessionsTableReferences),
      LocalPlaySessionRow,
      PrefetchHooks Function({
        bool localProfileId,
        bool serverInstanceId,
        bool playActivityOutboxRefs,
      })
    >;
typedef $$PlayActivityOutboxTableCreateCompanionBuilder =
    PlayActivityOutboxCompanion Function({
      required String sessionId,
      required String localProfileId,
      required String serverInstanceId,
      required DateTime queuedAt,
      Value<int> attemptCount,
      required DateTime nextAttemptAt,
      Value<int> rowid,
    });
typedef $$PlayActivityOutboxTableUpdateCompanionBuilder =
    PlayActivityOutboxCompanion Function({
      Value<String> sessionId,
      Value<String> localProfileId,
      Value<String> serverInstanceId,
      Value<DateTime> queuedAt,
      Value<int> attemptCount,
      Value<DateTime> nextAttemptAt,
      Value<int> rowid,
    });

final class $$PlayActivityOutboxTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $PlayActivityOutboxTable,
          PlayActivityOutboxRow
        > {
  $$PlayActivityOutboxTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalPlaySessionsTable _sessionIdTable(_$AppDatabase db) =>
      db.localPlaySessions.createAlias(
        'play_activity_outbox__session_id__local_play_sessions__session_id',
      );

  $$LocalPlaySessionsTableProcessedTableManager get sessionId {
    final $_column = $_itemColumn<String>('session_id')!;

    final manager = $$LocalPlaySessionsTableTableManager(
      $_db,
      $_db.localPlaySessions,
    ).filter((f) => f.sessionId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_sessionIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$PlayActivityOutboxTableFilterComposer
    extends Composer<_$AppDatabase, $PlayActivityOutboxTable> {
  $$PlayActivityOutboxTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get localProfileId => $composableBuilder(
    column: $table.localProfileId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get serverInstanceId => $composableBuilder(
    column: $table.serverInstanceId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get queuedAt => $composableBuilder(
    column: $table.queuedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get attemptCount => $composableBuilder(
    column: $table.attemptCount,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get nextAttemptAt => $composableBuilder(
    column: $table.nextAttemptAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalPlaySessionsTableFilterComposer get sessionId {
    final $$LocalPlaySessionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.sessionId,
      referencedTable: $db.localPlaySessions,
      getReferencedColumn: (t) => t.sessionId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalPlaySessionsTableFilterComposer(
            $db: $db,
            $table: $db.localPlaySessions,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PlayActivityOutboxTableOrderingComposer
    extends Composer<_$AppDatabase, $PlayActivityOutboxTable> {
  $$PlayActivityOutboxTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get localProfileId => $composableBuilder(
    column: $table.localProfileId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get serverInstanceId => $composableBuilder(
    column: $table.serverInstanceId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get queuedAt => $composableBuilder(
    column: $table.queuedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get attemptCount => $composableBuilder(
    column: $table.attemptCount,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get nextAttemptAt => $composableBuilder(
    column: $table.nextAttemptAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalPlaySessionsTableOrderingComposer get sessionId {
    final $$LocalPlaySessionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.sessionId,
      referencedTable: $db.localPlaySessions,
      getReferencedColumn: (t) => t.sessionId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalPlaySessionsTableOrderingComposer(
            $db: $db,
            $table: $db.localPlaySessions,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$PlayActivityOutboxTableAnnotationComposer
    extends Composer<_$AppDatabase, $PlayActivityOutboxTable> {
  $$PlayActivityOutboxTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get localProfileId => $composableBuilder(
    column: $table.localProfileId,
    builder: (column) => column,
  );

  GeneratedColumn<String> get serverInstanceId => $composableBuilder(
    column: $table.serverInstanceId,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get queuedAt =>
      $composableBuilder(column: $table.queuedAt, builder: (column) => column);

  GeneratedColumn<int> get attemptCount => $composableBuilder(
    column: $table.attemptCount,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get nextAttemptAt => $composableBuilder(
    column: $table.nextAttemptAt,
    builder: (column) => column,
  );

  $$LocalPlaySessionsTableAnnotationComposer get sessionId {
    final $$LocalPlaySessionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.sessionId,
          referencedTable: $db.localPlaySessions,
          getReferencedColumn: (t) => t.sessionId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$LocalPlaySessionsTableAnnotationComposer(
                $db: $db,
                $table: $db.localPlaySessions,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$PlayActivityOutboxTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $PlayActivityOutboxTable,
          PlayActivityOutboxRow,
          $$PlayActivityOutboxTableFilterComposer,
          $$PlayActivityOutboxTableOrderingComposer,
          $$PlayActivityOutboxTableAnnotationComposer,
          $$PlayActivityOutboxTableCreateCompanionBuilder,
          $$PlayActivityOutboxTableUpdateCompanionBuilder,
          (PlayActivityOutboxRow, $$PlayActivityOutboxTableReferences),
          PlayActivityOutboxRow,
          PrefetchHooks Function({bool sessionId})
        > {
  $$PlayActivityOutboxTableTableManager(
    _$AppDatabase db,
    $PlayActivityOutboxTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$PlayActivityOutboxTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$PlayActivityOutboxTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$PlayActivityOutboxTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> sessionId = const Value.absent(),
                Value<String> localProfileId = const Value.absent(),
                Value<String> serverInstanceId = const Value.absent(),
                Value<DateTime> queuedAt = const Value.absent(),
                Value<int> attemptCount = const Value.absent(),
                Value<DateTime> nextAttemptAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => PlayActivityOutboxCompanion(
                sessionId: sessionId,
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                queuedAt: queuedAt,
                attemptCount: attemptCount,
                nextAttemptAt: nextAttemptAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String sessionId,
                required String localProfileId,
                required String serverInstanceId,
                required DateTime queuedAt,
                Value<int> attemptCount = const Value.absent(),
                required DateTime nextAttemptAt,
                Value<int> rowid = const Value.absent(),
              }) => PlayActivityOutboxCompanion.insert(
                sessionId: sessionId,
                localProfileId: localProfileId,
                serverInstanceId: serverInstanceId,
                queuedAt: queuedAt,
                attemptCount: attemptCount,
                nextAttemptAt: nextAttemptAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$PlayActivityOutboxTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({sessionId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (sessionId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.sessionId,
                                referencedTable:
                                    $$PlayActivityOutboxTableReferences
                                        ._sessionIdTable(db),
                                referencedColumn:
                                    $$PlayActivityOutboxTableReferences
                                        ._sessionIdTable(db)
                                        .sessionId,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$PlayActivityOutboxTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $PlayActivityOutboxTable,
      PlayActivityOutboxRow,
      $$PlayActivityOutboxTableFilterComposer,
      $$PlayActivityOutboxTableOrderingComposer,
      $$PlayActivityOutboxTableAnnotationComposer,
      $$PlayActivityOutboxTableCreateCompanionBuilder,
      $$PlayActivityOutboxTableUpdateCompanionBuilder,
      (PlayActivityOutboxRow, $$PlayActivityOutboxTableReferences),
      PlayActivityOutboxRow,
      PrefetchHooks Function({bool sessionId})
    >;
typedef $$LegacyLocalInstallsTableCreateCompanionBuilder =
    LegacyLocalInstallsCompanion Function({
      required String releaseId,
      required String titleId,
      Value<String> titleName,
      Value<String?> platformId,
      Value<String?> platformName,
      required String platformShortName,
      Value<String?> coverUrl,
      Value<String> releaseName,
      Value<String?> releaseRevision,
      required String contentRoot,
      required String launchRelativePath,
      required int sizeBytes,
      Value<String?> primarySha256,
      required String manifestFingerprint,
      required String state,
      Value<String> installMode,
      required String manifestSnapshot,
      required DateTime installedAt,
      Value<DateTime?> lastPlayedAt,
      Value<int> rowid,
    });
typedef $$LegacyLocalInstallsTableUpdateCompanionBuilder =
    LegacyLocalInstallsCompanion Function({
      Value<String> releaseId,
      Value<String> titleId,
      Value<String> titleName,
      Value<String?> platformId,
      Value<String?> platformName,
      Value<String> platformShortName,
      Value<String?> coverUrl,
      Value<String> releaseName,
      Value<String?> releaseRevision,
      Value<String> contentRoot,
      Value<String> launchRelativePath,
      Value<int> sizeBytes,
      Value<String?> primarySha256,
      Value<String> manifestFingerprint,
      Value<String> state,
      Value<String> installMode,
      Value<String> manifestSnapshot,
      Value<DateTime> installedAt,
      Value<DateTime?> lastPlayedAt,
      Value<int> rowid,
    });

class $$LegacyLocalInstallsTableFilterComposer
    extends Composer<_$AppDatabase, $LegacyLocalInstallsTable> {
  $$LegacyLocalInstallsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleName => $composableBuilder(
    column: $table.titleName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get coverUrl => $composableBuilder(
    column: $table.coverUrl,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get state => $composableBuilder(
    column: $table.state,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnFilters(column),
  );
}

class $$LegacyLocalInstallsTableOrderingComposer
    extends Composer<_$AppDatabase, $LegacyLocalInstallsTable> {
  $$LegacyLocalInstallsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleName => $composableBuilder(
    column: $table.titleName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get coverUrl => $composableBuilder(
    column: $table.coverUrl,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get state => $composableBuilder(
    column: $table.state,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$LegacyLocalInstallsTableAnnotationComposer
    extends Composer<_$AppDatabase, $LegacyLocalInstallsTable> {
  $$LegacyLocalInstallsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get releaseId =>
      $composableBuilder(column: $table.releaseId, builder: (column) => column);

  GeneratedColumn<String> get titleId =>
      $composableBuilder(column: $table.titleId, builder: (column) => column);

  GeneratedColumn<String> get titleName =>
      $composableBuilder(column: $table.titleName, builder: (column) => column);

  GeneratedColumn<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => column,
  );

  GeneratedColumn<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get coverUrl =>
      $composableBuilder(column: $table.coverUrl, builder: (column) => column);

  GeneratedColumn<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => column,
  );

  GeneratedColumn<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => column,
  );

  GeneratedColumn<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => column,
  );

  GeneratedColumn<int> get sizeBytes =>
      $composableBuilder(column: $table.sizeBytes, builder: (column) => column);

  GeneratedColumn<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => column,
  );

  GeneratedColumn<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => column,
  );

  GeneratedColumn<String> get state =>
      $composableBuilder(column: $table.state, builder: (column) => column);

  GeneratedColumn<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => column,
  );

  GeneratedColumn<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => column,
  );
}

class $$LegacyLocalInstallsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $LegacyLocalInstallsTable,
          LegacyLocalInstallRow,
          $$LegacyLocalInstallsTableFilterComposer,
          $$LegacyLocalInstallsTableOrderingComposer,
          $$LegacyLocalInstallsTableAnnotationComposer,
          $$LegacyLocalInstallsTableCreateCompanionBuilder,
          $$LegacyLocalInstallsTableUpdateCompanionBuilder,
          (
            LegacyLocalInstallRow,
            BaseReferences<
              _$AppDatabase,
              $LegacyLocalInstallsTable,
              LegacyLocalInstallRow
            >,
          ),
          LegacyLocalInstallRow,
          PrefetchHooks Function()
        > {
  $$LegacyLocalInstallsTableTableManager(
    _$AppDatabase db,
    $LegacyLocalInstallsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$LegacyLocalInstallsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$LegacyLocalInstallsTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$LegacyLocalInstallsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> releaseId = const Value.absent(),
                Value<String> titleId = const Value.absent(),
                Value<String> titleName = const Value.absent(),
                Value<String?> platformId = const Value.absent(),
                Value<String?> platformName = const Value.absent(),
                Value<String> platformShortName = const Value.absent(),
                Value<String?> coverUrl = const Value.absent(),
                Value<String> releaseName = const Value.absent(),
                Value<String?> releaseRevision = const Value.absent(),
                Value<String> contentRoot = const Value.absent(),
                Value<String> launchRelativePath = const Value.absent(),
                Value<int> sizeBytes = const Value.absent(),
                Value<String?> primarySha256 = const Value.absent(),
                Value<String> manifestFingerprint = const Value.absent(),
                Value<String> state = const Value.absent(),
                Value<String> installMode = const Value.absent(),
                Value<String> manifestSnapshot = const Value.absent(),
                Value<DateTime> installedAt = const Value.absent(),
                Value<DateTime?> lastPlayedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LegacyLocalInstallsCompanion(
                releaseId: releaseId,
                titleId: titleId,
                titleName: titleName,
                platformId: platformId,
                platformName: platformName,
                platformShortName: platformShortName,
                coverUrl: coverUrl,
                releaseName: releaseName,
                releaseRevision: releaseRevision,
                contentRoot: contentRoot,
                launchRelativePath: launchRelativePath,
                sizeBytes: sizeBytes,
                primarySha256: primarySha256,
                manifestFingerprint: manifestFingerprint,
                state: state,
                installMode: installMode,
                manifestSnapshot: manifestSnapshot,
                installedAt: installedAt,
                lastPlayedAt: lastPlayedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String releaseId,
                required String titleId,
                Value<String> titleName = const Value.absent(),
                Value<String?> platformId = const Value.absent(),
                Value<String?> platformName = const Value.absent(),
                required String platformShortName,
                Value<String?> coverUrl = const Value.absent(),
                Value<String> releaseName = const Value.absent(),
                Value<String?> releaseRevision = const Value.absent(),
                required String contentRoot,
                required String launchRelativePath,
                required int sizeBytes,
                Value<String?> primarySha256 = const Value.absent(),
                required String manifestFingerprint,
                required String state,
                Value<String> installMode = const Value.absent(),
                required String manifestSnapshot,
                required DateTime installedAt,
                Value<DateTime?> lastPlayedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LegacyLocalInstallsCompanion.insert(
                releaseId: releaseId,
                titleId: titleId,
                titleName: titleName,
                platformId: platformId,
                platformName: platformName,
                platformShortName: platformShortName,
                coverUrl: coverUrl,
                releaseName: releaseName,
                releaseRevision: releaseRevision,
                contentRoot: contentRoot,
                launchRelativePath: launchRelativePath,
                sizeBytes: sizeBytes,
                primarySha256: primarySha256,
                manifestFingerprint: manifestFingerprint,
                state: state,
                installMode: installMode,
                manifestSnapshot: manifestSnapshot,
                installedAt: installedAt,
                lastPlayedAt: lastPlayedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$LegacyLocalInstallsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $LegacyLocalInstallsTable,
      LegacyLocalInstallRow,
      $$LegacyLocalInstallsTableFilterComposer,
      $$LegacyLocalInstallsTableOrderingComposer,
      $$LegacyLocalInstallsTableAnnotationComposer,
      $$LegacyLocalInstallsTableCreateCompanionBuilder,
      $$LegacyLocalInstallsTableUpdateCompanionBuilder,
      (
        LegacyLocalInstallRow,
        BaseReferences<
          _$AppDatabase,
          $LegacyLocalInstallsTable,
          LegacyLocalInstallRow
        >,
      ),
      LegacyLocalInstallRow,
      PrefetchHooks Function()
    >;
typedef $$LocalInstallsTableCreateCompanionBuilder =
    LocalInstallsCompanion Function({
      required String serverInstanceId,
      required String releaseId,
      required String titleId,
      Value<String> titleName,
      required String platformId,
      Value<String?> platformName,
      required String platformShortName,
      Value<String?> coverUrl,
      Value<String> releaseName,
      Value<String?> releaseRevision,
      required String contentRoot,
      required String launchRelativePath,
      required int sizeBytes,
      Value<String?> primarySha256,
      required String manifestFingerprint,
      required String state,
      Value<String> installMode,
      required String manifestSnapshot,
      required DateTime installedAt,
      Value<DateTime?> lastPlayedAt,
      Value<int> rowid,
    });
typedef $$LocalInstallsTableUpdateCompanionBuilder =
    LocalInstallsCompanion Function({
      Value<String> serverInstanceId,
      Value<String> releaseId,
      Value<String> titleId,
      Value<String> titleName,
      Value<String> platformId,
      Value<String?> platformName,
      Value<String> platformShortName,
      Value<String?> coverUrl,
      Value<String> releaseName,
      Value<String?> releaseRevision,
      Value<String> contentRoot,
      Value<String> launchRelativePath,
      Value<int> sizeBytes,
      Value<String?> primarySha256,
      Value<String> manifestFingerprint,
      Value<String> state,
      Value<String> installMode,
      Value<String> manifestSnapshot,
      Value<DateTime> installedAt,
      Value<DateTime?> lastPlayedAt,
      Value<int> rowid,
    });

final class $$LocalInstallsTableReferences
    extends
        BaseReferences<_$AppDatabase, $LocalInstallsTable, LocalInstallRow> {
  $$LocalInstallsTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $ServerConnectionsTable _serverInstanceIdTable(_$AppDatabase db) =>
      db.serverConnections.createAlias(
        'local_installs__server_instance_id__server_connections__instance_id',
      );

  $$ServerConnectionsTableProcessedTableManager get serverInstanceId {
    final $_column = $_itemColumn<String>('server_instance_id')!;

    final manager = $$ServerConnectionsTableTableManager(
      $_db,
      $_db.serverConnections,
    ).filter((f) => f.instanceId.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_serverInstanceIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$LocalInstallsTableFilterComposer
    extends Composer<_$AppDatabase, $LocalInstallsTable> {
  $$LocalInstallsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get titleName => $composableBuilder(
    column: $table.titleName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get coverUrl => $composableBuilder(
    column: $table.coverUrl,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get state => $composableBuilder(
    column: $table.state,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$ServerConnectionsTableFilterComposer get serverInstanceId {
    final $$ServerConnectionsTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableFilterComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$LocalInstallsTableOrderingComposer
    extends Composer<_$AppDatabase, $LocalInstallsTable> {
  $$LocalInstallsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get releaseId => $composableBuilder(
    column: $table.releaseId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleId => $composableBuilder(
    column: $table.titleId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get titleName => $composableBuilder(
    column: $table.titleName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get coverUrl => $composableBuilder(
    column: $table.coverUrl,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get state => $composableBuilder(
    column: $table.state,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$ServerConnectionsTableOrderingComposer get serverInstanceId {
    final $$ServerConnectionsTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.serverInstanceId,
      referencedTable: $db.serverConnections,
      getReferencedColumn: (t) => t.instanceId,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$ServerConnectionsTableOrderingComposer(
            $db: $db,
            $table: $db.serverConnections,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$LocalInstallsTableAnnotationComposer
    extends Composer<_$AppDatabase, $LocalInstallsTable> {
  $$LocalInstallsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get releaseId =>
      $composableBuilder(column: $table.releaseId, builder: (column) => column);

  GeneratedColumn<String> get titleId =>
      $composableBuilder(column: $table.titleId, builder: (column) => column);

  GeneratedColumn<String> get titleName =>
      $composableBuilder(column: $table.titleName, builder: (column) => column);

  GeneratedColumn<String> get platformId => $composableBuilder(
    column: $table.platformId,
    builder: (column) => column,
  );

  GeneratedColumn<String> get platformName => $composableBuilder(
    column: $table.platformName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get platformShortName => $composableBuilder(
    column: $table.platformShortName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get coverUrl =>
      $composableBuilder(column: $table.coverUrl, builder: (column) => column);

  GeneratedColumn<String> get releaseName => $composableBuilder(
    column: $table.releaseName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get releaseRevision => $composableBuilder(
    column: $table.releaseRevision,
    builder: (column) => column,
  );

  GeneratedColumn<String> get contentRoot => $composableBuilder(
    column: $table.contentRoot,
    builder: (column) => column,
  );

  GeneratedColumn<String> get launchRelativePath => $composableBuilder(
    column: $table.launchRelativePath,
    builder: (column) => column,
  );

  GeneratedColumn<int> get sizeBytes =>
      $composableBuilder(column: $table.sizeBytes, builder: (column) => column);

  GeneratedColumn<String> get primarySha256 => $composableBuilder(
    column: $table.primarySha256,
    builder: (column) => column,
  );

  GeneratedColumn<String> get manifestFingerprint => $composableBuilder(
    column: $table.manifestFingerprint,
    builder: (column) => column,
  );

  GeneratedColumn<String> get state =>
      $composableBuilder(column: $table.state, builder: (column) => column);

  GeneratedColumn<String> get installMode => $composableBuilder(
    column: $table.installMode,
    builder: (column) => column,
  );

  GeneratedColumn<String> get manifestSnapshot => $composableBuilder(
    column: $table.manifestSnapshot,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get installedAt => $composableBuilder(
    column: $table.installedAt,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get lastPlayedAt => $composableBuilder(
    column: $table.lastPlayedAt,
    builder: (column) => column,
  );

  $$ServerConnectionsTableAnnotationComposer get serverInstanceId {
    final $$ServerConnectionsTableAnnotationComposer composer =
        $composerBuilder(
          composer: this,
          getCurrentColumn: (t) => t.serverInstanceId,
          referencedTable: $db.serverConnections,
          getReferencedColumn: (t) => t.instanceId,
          builder:
              (
                joinBuilder, {
                $addJoinBuilderToRootComposer,
                $removeJoinBuilderFromRootComposer,
              }) => $$ServerConnectionsTableAnnotationComposer(
                $db: $db,
                $table: $db.serverConnections,
                $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
                joinBuilder: joinBuilder,
                $removeJoinBuilderFromRootComposer:
                    $removeJoinBuilderFromRootComposer,
              ),
        );
    return composer;
  }
}

class $$LocalInstallsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $LocalInstallsTable,
          LocalInstallRow,
          $$LocalInstallsTableFilterComposer,
          $$LocalInstallsTableOrderingComposer,
          $$LocalInstallsTableAnnotationComposer,
          $$LocalInstallsTableCreateCompanionBuilder,
          $$LocalInstallsTableUpdateCompanionBuilder,
          (LocalInstallRow, $$LocalInstallsTableReferences),
          LocalInstallRow,
          PrefetchHooks Function({bool serverInstanceId})
        > {
  $$LocalInstallsTableTableManager(_$AppDatabase db, $LocalInstallsTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$LocalInstallsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$LocalInstallsTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$LocalInstallsTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> serverInstanceId = const Value.absent(),
                Value<String> releaseId = const Value.absent(),
                Value<String> titleId = const Value.absent(),
                Value<String> titleName = const Value.absent(),
                Value<String> platformId = const Value.absent(),
                Value<String?> platformName = const Value.absent(),
                Value<String> platformShortName = const Value.absent(),
                Value<String?> coverUrl = const Value.absent(),
                Value<String> releaseName = const Value.absent(),
                Value<String?> releaseRevision = const Value.absent(),
                Value<String> contentRoot = const Value.absent(),
                Value<String> launchRelativePath = const Value.absent(),
                Value<int> sizeBytes = const Value.absent(),
                Value<String?> primarySha256 = const Value.absent(),
                Value<String> manifestFingerprint = const Value.absent(),
                Value<String> state = const Value.absent(),
                Value<String> installMode = const Value.absent(),
                Value<String> manifestSnapshot = const Value.absent(),
                Value<DateTime> installedAt = const Value.absent(),
                Value<DateTime?> lastPlayedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LocalInstallsCompanion(
                serverInstanceId: serverInstanceId,
                releaseId: releaseId,
                titleId: titleId,
                titleName: titleName,
                platformId: platformId,
                platformName: platformName,
                platformShortName: platformShortName,
                coverUrl: coverUrl,
                releaseName: releaseName,
                releaseRevision: releaseRevision,
                contentRoot: contentRoot,
                launchRelativePath: launchRelativePath,
                sizeBytes: sizeBytes,
                primarySha256: primarySha256,
                manifestFingerprint: manifestFingerprint,
                state: state,
                installMode: installMode,
                manifestSnapshot: manifestSnapshot,
                installedAt: installedAt,
                lastPlayedAt: lastPlayedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String serverInstanceId,
                required String releaseId,
                required String titleId,
                Value<String> titleName = const Value.absent(),
                required String platformId,
                Value<String?> platformName = const Value.absent(),
                required String platformShortName,
                Value<String?> coverUrl = const Value.absent(),
                Value<String> releaseName = const Value.absent(),
                Value<String?> releaseRevision = const Value.absent(),
                required String contentRoot,
                required String launchRelativePath,
                required int sizeBytes,
                Value<String?> primarySha256 = const Value.absent(),
                required String manifestFingerprint,
                required String state,
                Value<String> installMode = const Value.absent(),
                required String manifestSnapshot,
                required DateTime installedAt,
                Value<DateTime?> lastPlayedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => LocalInstallsCompanion.insert(
                serverInstanceId: serverInstanceId,
                releaseId: releaseId,
                titleId: titleId,
                titleName: titleName,
                platformId: platformId,
                platformName: platformName,
                platformShortName: platformShortName,
                coverUrl: coverUrl,
                releaseName: releaseName,
                releaseRevision: releaseRevision,
                contentRoot: contentRoot,
                launchRelativePath: launchRelativePath,
                sizeBytes: sizeBytes,
                primarySha256: primarySha256,
                manifestFingerprint: manifestFingerprint,
                state: state,
                installMode: installMode,
                manifestSnapshot: manifestSnapshot,
                installedAt: installedAt,
                lastPlayedAt: lastPlayedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$LocalInstallsTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({serverInstanceId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (serverInstanceId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.serverInstanceId,
                                referencedTable: $$LocalInstallsTableReferences
                                    ._serverInstanceIdTable(db),
                                referencedColumn: $$LocalInstallsTableReferences
                                    ._serverInstanceIdTable(db)
                                    .instanceId,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$LocalInstallsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $LocalInstallsTable,
      LocalInstallRow,
      $$LocalInstallsTableFilterComposer,
      $$LocalInstallsTableOrderingComposer,
      $$LocalInstallsTableAnnotationComposer,
      $$LocalInstallsTableCreateCompanionBuilder,
      $$LocalInstallsTableUpdateCompanionBuilder,
      (LocalInstallRow, $$LocalInstallsTableReferences),
      LocalInstallRow,
      PrefetchHooks Function({bool serverInstanceId})
    >;
typedef $$RuntimeOverrideRulesTableCreateCompanionBuilder =
    RuntimeOverrideRulesCompanion Function({
      required String scope,
      required String scopeValue,
      required String profileId,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$RuntimeOverrideRulesTableUpdateCompanionBuilder =
    RuntimeOverrideRulesCompanion Function({
      Value<String> scope,
      Value<String> scopeValue,
      Value<String> profileId,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

class $$RuntimeOverrideRulesTableFilterComposer
    extends Composer<_$AppDatabase, $RuntimeOverrideRulesTable> {
  $$RuntimeOverrideRulesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get profileId => $composableBuilder(
    column: $table.profileId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );
}

class $$RuntimeOverrideRulesTableOrderingComposer
    extends Composer<_$AppDatabase, $RuntimeOverrideRulesTable> {
  $$RuntimeOverrideRulesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get profileId => $composableBuilder(
    column: $table.profileId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$RuntimeOverrideRulesTableAnnotationComposer
    extends Composer<_$AppDatabase, $RuntimeOverrideRulesTable> {
  $$RuntimeOverrideRulesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get scope =>
      $composableBuilder(column: $table.scope, builder: (column) => column);

  GeneratedColumn<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => column,
  );

  GeneratedColumn<String> get profileId =>
      $composableBuilder(column: $table.profileId, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);
}

class $$RuntimeOverrideRulesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $RuntimeOverrideRulesTable,
          RuntimeOverrideRuleRow,
          $$RuntimeOverrideRulesTableFilterComposer,
          $$RuntimeOverrideRulesTableOrderingComposer,
          $$RuntimeOverrideRulesTableAnnotationComposer,
          $$RuntimeOverrideRulesTableCreateCompanionBuilder,
          $$RuntimeOverrideRulesTableUpdateCompanionBuilder,
          (
            RuntimeOverrideRuleRow,
            BaseReferences<
              _$AppDatabase,
              $RuntimeOverrideRulesTable,
              RuntimeOverrideRuleRow
            >,
          ),
          RuntimeOverrideRuleRow,
          PrefetchHooks Function()
        > {
  $$RuntimeOverrideRulesTableTableManager(
    _$AppDatabase db,
    $RuntimeOverrideRulesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$RuntimeOverrideRulesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$RuntimeOverrideRulesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$RuntimeOverrideRulesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> scope = const Value.absent(),
                Value<String> scopeValue = const Value.absent(),
                Value<String> profileId = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => RuntimeOverrideRulesCompanion(
                scope: scope,
                scopeValue: scopeValue,
                profileId: profileId,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String scope,
                required String scopeValue,
                required String profileId,
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => RuntimeOverrideRulesCompanion.insert(
                scope: scope,
                scopeValue: scopeValue,
                profileId: profileId,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$RuntimeOverrideRulesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $RuntimeOverrideRulesTable,
      RuntimeOverrideRuleRow,
      $$RuntimeOverrideRulesTableFilterComposer,
      $$RuntimeOverrideRulesTableOrderingComposer,
      $$RuntimeOverrideRulesTableAnnotationComposer,
      $$RuntimeOverrideRulesTableCreateCompanionBuilder,
      $$RuntimeOverrideRulesTableUpdateCompanionBuilder,
      (
        RuntimeOverrideRuleRow,
        BaseReferences<
          _$AppDatabase,
          $RuntimeOverrideRulesTable,
          RuntimeOverrideRuleRow
        >,
      ),
      RuntimeOverrideRuleRow,
      PrefetchHooks Function()
    >;
typedef $$ControllerBindingRulesTableCreateCompanionBuilder =
    ControllerBindingRulesCompanion Function({
      required String scope,
      required String scopeValue,
      required String action,
      Value<String?> button,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$ControllerBindingRulesTableUpdateCompanionBuilder =
    ControllerBindingRulesCompanion Function({
      Value<String> scope,
      Value<String> scopeValue,
      Value<String> action,
      Value<String?> button,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

class $$ControllerBindingRulesTableFilterComposer
    extends Composer<_$AppDatabase, $ControllerBindingRulesTable> {
  $$ControllerBindingRulesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get action => $composableBuilder(
    column: $table.action,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get button => $composableBuilder(
    column: $table.button,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );
}

class $$ControllerBindingRulesTableOrderingComposer
    extends Composer<_$AppDatabase, $ControllerBindingRulesTable> {
  $$ControllerBindingRulesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get action => $composableBuilder(
    column: $table.action,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get button => $composableBuilder(
    column: $table.button,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ControllerBindingRulesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ControllerBindingRulesTable> {
  $$ControllerBindingRulesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get scope =>
      $composableBuilder(column: $table.scope, builder: (column) => column);

  GeneratedColumn<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => column,
  );

  GeneratedColumn<String> get action =>
      $composableBuilder(column: $table.action, builder: (column) => column);

  GeneratedColumn<String> get button =>
      $composableBuilder(column: $table.button, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);
}

class $$ControllerBindingRulesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ControllerBindingRulesTable,
          ControllerBindingRuleRow,
          $$ControllerBindingRulesTableFilterComposer,
          $$ControllerBindingRulesTableOrderingComposer,
          $$ControllerBindingRulesTableAnnotationComposer,
          $$ControllerBindingRulesTableCreateCompanionBuilder,
          $$ControllerBindingRulesTableUpdateCompanionBuilder,
          (
            ControllerBindingRuleRow,
            BaseReferences<
              _$AppDatabase,
              $ControllerBindingRulesTable,
              ControllerBindingRuleRow
            >,
          ),
          ControllerBindingRuleRow,
          PrefetchHooks Function()
        > {
  $$ControllerBindingRulesTableTableManager(
    _$AppDatabase db,
    $ControllerBindingRulesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ControllerBindingRulesTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$ControllerBindingRulesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ControllerBindingRulesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> scope = const Value.absent(),
                Value<String> scopeValue = const Value.absent(),
                Value<String> action = const Value.absent(),
                Value<String?> button = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ControllerBindingRulesCompanion(
                scope: scope,
                scopeValue: scopeValue,
                action: action,
                button: button,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String scope,
                required String scopeValue,
                required String action,
                Value<String?> button = const Value.absent(),
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => ControllerBindingRulesCompanion.insert(
                scope: scope,
                scopeValue: scopeValue,
                action: action,
                button: button,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$ControllerBindingRulesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ControllerBindingRulesTable,
      ControllerBindingRuleRow,
      $$ControllerBindingRulesTableFilterComposer,
      $$ControllerBindingRulesTableOrderingComposer,
      $$ControllerBindingRulesTableAnnotationComposer,
      $$ControllerBindingRulesTableCreateCompanionBuilder,
      $$ControllerBindingRulesTableUpdateCompanionBuilder,
      (
        ControllerBindingRuleRow,
        BaseReferences<
          _$AppDatabase,
          $ControllerBindingRulesTable,
          ControllerBindingRuleRow
        >,
      ),
      ControllerBindingRuleRow,
      PrefetchHooks Function()
    >;
typedef $$ControllerMappingProfilesTableCreateCompanionBuilder =
    ControllerMappingProfilesCompanion Function({
      required String localProfileId,
      required String sdlGuid,
      required String displayName,
      Value<String?> templateId,
      required DateTime createdAt,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$ControllerMappingProfilesTableUpdateCompanionBuilder =
    ControllerMappingProfilesCompanion Function({
      Value<String> localProfileId,
      Value<String> sdlGuid,
      Value<String> displayName,
      Value<String?> templateId,
      Value<DateTime> createdAt,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

final class $$ControllerMappingProfilesTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $ControllerMappingProfilesTable,
          ControllerMappingProfileRow
        > {
  $$ControllerMappingProfilesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(_$AppDatabase db) =>
      db.localProfiles.createAlias(
        'controller_mapping_profiles__local_profile_id__local_profiles__id',
      );

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$ControllerMappingProfilesTableFilterComposer
    extends Composer<_$AppDatabase, $ControllerMappingProfilesTable> {
  $$ControllerMappingProfilesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerMappingProfilesTableOrderingComposer
    extends Composer<_$AppDatabase, $ControllerMappingProfilesTable> {
  $$ControllerMappingProfilesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerMappingProfilesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ControllerMappingProfilesTable> {
  $$ControllerMappingProfilesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get sdlGuid =>
      $composableBuilder(column: $table.sdlGuid, builder: (column) => column);

  GeneratedColumn<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => column,
  );

  GeneratedColumn<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerMappingProfilesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ControllerMappingProfilesTable,
          ControllerMappingProfileRow,
          $$ControllerMappingProfilesTableFilterComposer,
          $$ControllerMappingProfilesTableOrderingComposer,
          $$ControllerMappingProfilesTableAnnotationComposer,
          $$ControllerMappingProfilesTableCreateCompanionBuilder,
          $$ControllerMappingProfilesTableUpdateCompanionBuilder,
          (
            ControllerMappingProfileRow,
            $$ControllerMappingProfilesTableReferences,
          ),
          ControllerMappingProfileRow,
          PrefetchHooks Function({bool localProfileId})
        > {
  $$ControllerMappingProfilesTableTableManager(
    _$AppDatabase db,
    $ControllerMappingProfilesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ControllerMappingProfilesTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$ControllerMappingProfilesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ControllerMappingProfilesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> sdlGuid = const Value.absent(),
                Value<String> displayName = const Value.absent(),
                Value<String?> templateId = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ControllerMappingProfilesCompanion(
                localProfileId: localProfileId,
                sdlGuid: sdlGuid,
                displayName: displayName,
                templateId: templateId,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String sdlGuid,
                required String displayName,
                Value<String?> templateId = const Value.absent(),
                required DateTime createdAt,
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => ControllerMappingProfilesCompanion.insert(
                localProfileId: localProfileId,
                sdlGuid: sdlGuid,
                displayName: displayName,
                templateId: templateId,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$ControllerMappingProfilesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({localProfileId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (localProfileId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.localProfileId,
                                referencedTable:
                                    $$ControllerMappingProfilesTableReferences
                                        ._localProfileIdTable(db),
                                referencedColumn:
                                    $$ControllerMappingProfilesTableReferences
                                        ._localProfileIdTable(db)
                                        .id,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$ControllerMappingProfilesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ControllerMappingProfilesTable,
      ControllerMappingProfileRow,
      $$ControllerMappingProfilesTableFilterComposer,
      $$ControllerMappingProfilesTableOrderingComposer,
      $$ControllerMappingProfilesTableAnnotationComposer,
      $$ControllerMappingProfilesTableCreateCompanionBuilder,
      $$ControllerMappingProfilesTableUpdateCompanionBuilder,
      (ControllerMappingProfileRow, $$ControllerMappingProfilesTableReferences),
      ControllerMappingProfileRow,
      PrefetchHooks Function({bool localProfileId})
    >;
typedef $$ControllerProfileBindingRulesTableCreateCompanionBuilder =
    ControllerProfileBindingRulesCompanion Function({
      required String localProfileId,
      required String sdlGuid,
      required String scope,
      required String scopeValue,
      required String action,
      Value<String?> button,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$ControllerProfileBindingRulesTableUpdateCompanionBuilder =
    ControllerProfileBindingRulesCompanion Function({
      Value<String> localProfileId,
      Value<String> sdlGuid,
      Value<String> scope,
      Value<String> scopeValue,
      Value<String> action,
      Value<String?> button,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

final class $$ControllerProfileBindingRulesTableReferences
    extends
        BaseReferences<
          _$AppDatabase,
          $ControllerProfileBindingRulesTable,
          ControllerProfileBindingRuleRow
        > {
  $$ControllerProfileBindingRulesTableReferences(
    super.$_db,
    super.$_table,
    super.$_typedResult,
  );

  static $LocalProfilesTable _localProfileIdTable(
    _$AppDatabase db,
  ) => db.localProfiles.createAlias(
    'controller_profile_binding_rules__local_profile_id__local_profiles__id',
  );

  $$LocalProfilesTableProcessedTableManager get localProfileId {
    final $_column = $_itemColumn<String>('local_profile_id')!;

    final manager = $$LocalProfilesTableTableManager(
      $_db,
      $_db.localProfiles,
    ).filter((f) => f.id.sqlEquals($_column));
    final item = $_typedResult.readTableOrNull(_localProfileIdTable($_db));
    if (item == null) return manager;
    return ProcessedTableManager(
      manager.$state.copyWith(prefetchedData: [item]),
    );
  }
}

class $$ControllerProfileBindingRulesTableFilterComposer
    extends Composer<_$AppDatabase, $ControllerProfileBindingRulesTable> {
  $$ControllerProfileBindingRulesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get action => $composableBuilder(
    column: $table.action,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get button => $composableBuilder(
    column: $table.button,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );

  $$LocalProfilesTableFilterComposer get localProfileId {
    final $$LocalProfilesTableFilterComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableFilterComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerProfileBindingRulesTableOrderingComposer
    extends Composer<_$AppDatabase, $ControllerProfileBindingRulesTable> {
  $$ControllerProfileBindingRulesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get scope => $composableBuilder(
    column: $table.scope,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get action => $composableBuilder(
    column: $table.action,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get button => $composableBuilder(
    column: $table.button,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );

  $$LocalProfilesTableOrderingComposer get localProfileId {
    final $$LocalProfilesTableOrderingComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableOrderingComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerProfileBindingRulesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ControllerProfileBindingRulesTable> {
  $$ControllerProfileBindingRulesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get sdlGuid =>
      $composableBuilder(column: $table.sdlGuid, builder: (column) => column);

  GeneratedColumn<String> get scope =>
      $composableBuilder(column: $table.scope, builder: (column) => column);

  GeneratedColumn<String> get scopeValue => $composableBuilder(
    column: $table.scopeValue,
    builder: (column) => column,
  );

  GeneratedColumn<String> get action =>
      $composableBuilder(column: $table.action, builder: (column) => column);

  GeneratedColumn<String> get button =>
      $composableBuilder(column: $table.button, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);

  $$LocalProfilesTableAnnotationComposer get localProfileId {
    final $$LocalProfilesTableAnnotationComposer composer = $composerBuilder(
      composer: this,
      getCurrentColumn: (t) => t.localProfileId,
      referencedTable: $db.localProfiles,
      getReferencedColumn: (t) => t.id,
      builder:
          (
            joinBuilder, {
            $addJoinBuilderToRootComposer,
            $removeJoinBuilderFromRootComposer,
          }) => $$LocalProfilesTableAnnotationComposer(
            $db: $db,
            $table: $db.localProfiles,
            $addJoinBuilderToRootComposer: $addJoinBuilderToRootComposer,
            joinBuilder: joinBuilder,
            $removeJoinBuilderFromRootComposer:
                $removeJoinBuilderFromRootComposer,
          ),
    );
    return composer;
  }
}

class $$ControllerProfileBindingRulesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ControllerProfileBindingRulesTable,
          ControllerProfileBindingRuleRow,
          $$ControllerProfileBindingRulesTableFilterComposer,
          $$ControllerProfileBindingRulesTableOrderingComposer,
          $$ControllerProfileBindingRulesTableAnnotationComposer,
          $$ControllerProfileBindingRulesTableCreateCompanionBuilder,
          $$ControllerProfileBindingRulesTableUpdateCompanionBuilder,
          (
            ControllerProfileBindingRuleRow,
            $$ControllerProfileBindingRulesTableReferences,
          ),
          ControllerProfileBindingRuleRow,
          PrefetchHooks Function({bool localProfileId})
        > {
  $$ControllerProfileBindingRulesTableTableManager(
    _$AppDatabase db,
    $ControllerProfileBindingRulesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ControllerProfileBindingRulesTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$ControllerProfileBindingRulesTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ControllerProfileBindingRulesTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> localProfileId = const Value.absent(),
                Value<String> sdlGuid = const Value.absent(),
                Value<String> scope = const Value.absent(),
                Value<String> scopeValue = const Value.absent(),
                Value<String> action = const Value.absent(),
                Value<String?> button = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ControllerProfileBindingRulesCompanion(
                localProfileId: localProfileId,
                sdlGuid: sdlGuid,
                scope: scope,
                scopeValue: scopeValue,
                action: action,
                button: button,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String localProfileId,
                required String sdlGuid,
                required String scope,
                required String scopeValue,
                required String action,
                Value<String?> button = const Value.absent(),
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => ControllerProfileBindingRulesCompanion.insert(
                localProfileId: localProfileId,
                sdlGuid: sdlGuid,
                scope: scope,
                scopeValue: scopeValue,
                action: action,
                button: button,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable(table),
                  $$ControllerProfileBindingRulesTableReferences(db, table, e),
                ),
              )
              .toList(),
          prefetchHooksCallback: ({localProfileId = false}) {
            return PrefetchHooks(
              db: db,
              explicitlyWatchedTables: [],
              addJoins:
                  <
                    T extends TableManagerState<
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic,
                      dynamic
                    >
                  >(state) {
                    if (localProfileId) {
                      state =
                          state.withJoin(
                                currentTable: table,
                                currentColumn: table.localProfileId,
                                referencedTable:
                                    $$ControllerProfileBindingRulesTableReferences
                                        ._localProfileIdTable(db),
                                referencedColumn:
                                    $$ControllerProfileBindingRulesTableReferences
                                        ._localProfileIdTable(db)
                                        .id,
                              )
                              as T;
                    }

                    return state;
                  },
              getPrefetchedDataCallback: (items) async {
                return [];
              },
            );
          },
        ),
      );
}

typedef $$ControllerProfileBindingRulesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ControllerProfileBindingRulesTable,
      ControllerProfileBindingRuleRow,
      $$ControllerProfileBindingRulesTableFilterComposer,
      $$ControllerProfileBindingRulesTableOrderingComposer,
      $$ControllerProfileBindingRulesTableAnnotationComposer,
      $$ControllerProfileBindingRulesTableCreateCompanionBuilder,
      $$ControllerProfileBindingRulesTableUpdateCompanionBuilder,
      (
        ControllerProfileBindingRuleRow,
        $$ControllerProfileBindingRulesTableReferences,
      ),
      ControllerProfileBindingRuleRow,
      PrefetchHooks Function({bool localProfileId})
    >;
typedef $$ControllerPreferencesRowsTableCreateCompanionBuilder =
    ControllerPreferencesRowsCompanion Function({
      Value<int> id,
      Value<String?> templateId,
      required DateTime updatedAt,
    });
typedef $$ControllerPreferencesRowsTableUpdateCompanionBuilder =
    ControllerPreferencesRowsCompanion Function({
      Value<int> id,
      Value<String?> templateId,
      Value<DateTime> updatedAt,
    });

class $$ControllerPreferencesRowsTableFilterComposer
    extends Composer<_$AppDatabase, $ControllerPreferencesRowsTable> {
  $$ControllerPreferencesRowsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<int> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );
}

class $$ControllerPreferencesRowsTableOrderingComposer
    extends Composer<_$AppDatabase, $ControllerPreferencesRowsTable> {
  $$ControllerPreferencesRowsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<int> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ControllerPreferencesRowsTableAnnotationComposer
    extends Composer<_$AppDatabase, $ControllerPreferencesRowsTable> {
  $$ControllerPreferencesRowsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<int> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get templateId => $composableBuilder(
    column: $table.templateId,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);
}

class $$ControllerPreferencesRowsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ControllerPreferencesRowsTable,
          ControllerPreferencesRow,
          $$ControllerPreferencesRowsTableFilterComposer,
          $$ControllerPreferencesRowsTableOrderingComposer,
          $$ControllerPreferencesRowsTableAnnotationComposer,
          $$ControllerPreferencesRowsTableCreateCompanionBuilder,
          $$ControllerPreferencesRowsTableUpdateCompanionBuilder,
          (
            ControllerPreferencesRow,
            BaseReferences<
              _$AppDatabase,
              $ControllerPreferencesRowsTable,
              ControllerPreferencesRow
            >,
          ),
          ControllerPreferencesRow,
          PrefetchHooks Function()
        > {
  $$ControllerPreferencesRowsTableTableManager(
    _$AppDatabase db,
    $ControllerPreferencesRowsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ControllerPreferencesRowsTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$ControllerPreferencesRowsTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ControllerPreferencesRowsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<int> id = const Value.absent(),
                Value<String?> templateId = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
              }) => ControllerPreferencesRowsCompanion(
                id: id,
                templateId: templateId,
                updatedAt: updatedAt,
              ),
          createCompanionCallback:
              ({
                Value<int> id = const Value.absent(),
                Value<String?> templateId = const Value.absent(),
                required DateTime updatedAt,
              }) => ControllerPreferencesRowsCompanion.insert(
                id: id,
                templateId: templateId,
                updatedAt: updatedAt,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$ControllerPreferencesRowsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ControllerPreferencesRowsTable,
      ControllerPreferencesRow,
      $$ControllerPreferencesRowsTableFilterComposer,
      $$ControllerPreferencesRowsTableOrderingComposer,
      $$ControllerPreferencesRowsTableAnnotationComposer,
      $$ControllerPreferencesRowsTableCreateCompanionBuilder,
      $$ControllerPreferencesRowsTableUpdateCompanionBuilder,
      (
        ControllerPreferencesRow,
        BaseReferences<
          _$AppDatabase,
          $ControllerPreferencesRowsTable,
          ControllerPreferencesRow
        >,
      ),
      ControllerPreferencesRow,
      PrefetchHooks Function()
    >;
typedef $$ControllerHardwareMappingsTableCreateCompanionBuilder =
    ControllerHardwareMappingsCompanion Function({
      required String sdlPlatform,
      required String sdlGuid,
      required String displayName,
      required int mappingFormatVersion,
      required String mappingData,
      required DateTime createdAt,
      required DateTime updatedAt,
      Value<int> rowid,
    });
typedef $$ControllerHardwareMappingsTableUpdateCompanionBuilder =
    ControllerHardwareMappingsCompanion Function({
      Value<String> sdlPlatform,
      Value<String> sdlGuid,
      Value<String> displayName,
      Value<int> mappingFormatVersion,
      Value<String> mappingData,
      Value<DateTime> createdAt,
      Value<DateTime> updatedAt,
      Value<int> rowid,
    });

class $$ControllerHardwareMappingsTableFilterComposer
    extends Composer<_$AppDatabase, $ControllerHardwareMappingsTable> {
  $$ControllerHardwareMappingsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get sdlPlatform => $composableBuilder(
    column: $table.sdlPlatform,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get mappingFormatVersion => $composableBuilder(
    column: $table.mappingFormatVersion,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get mappingData => $composableBuilder(
    column: $table.mappingData,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnFilters(column),
  );
}

class $$ControllerHardwareMappingsTableOrderingComposer
    extends Composer<_$AppDatabase, $ControllerHardwareMappingsTable> {
  $$ControllerHardwareMappingsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get sdlPlatform => $composableBuilder(
    column: $table.sdlPlatform,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get sdlGuid => $composableBuilder(
    column: $table.sdlGuid,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get mappingFormatVersion => $composableBuilder(
    column: $table.mappingFormatVersion,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get mappingData => $composableBuilder(
    column: $table.mappingData,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get updatedAt => $composableBuilder(
    column: $table.updatedAt,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ControllerHardwareMappingsTableAnnotationComposer
    extends Composer<_$AppDatabase, $ControllerHardwareMappingsTable> {
  $$ControllerHardwareMappingsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get sdlPlatform => $composableBuilder(
    column: $table.sdlPlatform,
    builder: (column) => column,
  );

  GeneratedColumn<String> get sdlGuid =>
      $composableBuilder(column: $table.sdlGuid, builder: (column) => column);

  GeneratedColumn<String> get displayName => $composableBuilder(
    column: $table.displayName,
    builder: (column) => column,
  );

  GeneratedColumn<int> get mappingFormatVersion => $composableBuilder(
    column: $table.mappingFormatVersion,
    builder: (column) => column,
  );

  GeneratedColumn<String> get mappingData => $composableBuilder(
    column: $table.mappingData,
    builder: (column) => column,
  );

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<DateTime> get updatedAt =>
      $composableBuilder(column: $table.updatedAt, builder: (column) => column);
}

class $$ControllerHardwareMappingsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ControllerHardwareMappingsTable,
          ControllerHardwareMappingRow,
          $$ControllerHardwareMappingsTableFilterComposer,
          $$ControllerHardwareMappingsTableOrderingComposer,
          $$ControllerHardwareMappingsTableAnnotationComposer,
          $$ControllerHardwareMappingsTableCreateCompanionBuilder,
          $$ControllerHardwareMappingsTableUpdateCompanionBuilder,
          (
            ControllerHardwareMappingRow,
            BaseReferences<
              _$AppDatabase,
              $ControllerHardwareMappingsTable,
              ControllerHardwareMappingRow
            >,
          ),
          ControllerHardwareMappingRow,
          PrefetchHooks Function()
        > {
  $$ControllerHardwareMappingsTableTableManager(
    _$AppDatabase db,
    $ControllerHardwareMappingsTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ControllerHardwareMappingsTableFilterComposer(
                $db: db,
                $table: table,
              ),
          createOrderingComposer: () =>
              $$ControllerHardwareMappingsTableOrderingComposer(
                $db: db,
                $table: table,
              ),
          createComputedFieldComposer: () =>
              $$ControllerHardwareMappingsTableAnnotationComposer(
                $db: db,
                $table: table,
              ),
          updateCompanionCallback:
              ({
                Value<String> sdlPlatform = const Value.absent(),
                Value<String> sdlGuid = const Value.absent(),
                Value<String> displayName = const Value.absent(),
                Value<int> mappingFormatVersion = const Value.absent(),
                Value<String> mappingData = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<DateTime> updatedAt = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ControllerHardwareMappingsCompanion(
                sdlPlatform: sdlPlatform,
                sdlGuid: sdlGuid,
                displayName: displayName,
                mappingFormatVersion: mappingFormatVersion,
                mappingData: mappingData,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String sdlPlatform,
                required String sdlGuid,
                required String displayName,
                required int mappingFormatVersion,
                required String mappingData,
                required DateTime createdAt,
                required DateTime updatedAt,
                Value<int> rowid = const Value.absent(),
              }) => ControllerHardwareMappingsCompanion.insert(
                sdlPlatform: sdlPlatform,
                sdlGuid: sdlGuid,
                displayName: displayName,
                mappingFormatVersion: mappingFormatVersion,
                mappingData: mappingData,
                createdAt: createdAt,
                updatedAt: updatedAt,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$ControllerHardwareMappingsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ControllerHardwareMappingsTable,
      ControllerHardwareMappingRow,
      $$ControllerHardwareMappingsTableFilterComposer,
      $$ControllerHardwareMappingsTableOrderingComposer,
      $$ControllerHardwareMappingsTableAnnotationComposer,
      $$ControllerHardwareMappingsTableCreateCompanionBuilder,
      $$ControllerHardwareMappingsTableUpdateCompanionBuilder,
      (
        ControllerHardwareMappingRow,
        BaseReferences<
          _$AppDatabase,
          $ControllerHardwareMappingsTable,
          ControllerHardwareMappingRow
        >,
      ),
      ControllerHardwareMappingRow,
      PrefetchHooks Function()
    >;

class $AppDatabaseManager {
  final _$AppDatabase _db;
  $AppDatabaseManager(this._db);
  $$ServerConnectionsTableTableManager get serverConnections =>
      $$ServerConnectionsTableTableManager(_db, _db.serverConnections);
  $$LocalProfilesTableTableManager get localProfiles =>
      $$LocalProfilesTableTableManager(_db, _db.localProfiles);
  $$PendingServerLocatorsTableTableManager get pendingServerLocators =>
      $$PendingServerLocatorsTableTableManager(_db, _db.pendingServerLocators);
  $$RomdAccountLinksTableTableManager get romdAccountLinks =>
      $$RomdAccountLinksTableTableManager(_db, _db.romdAccountLinks);
  $$ProfileLocalGamesTableTableManager get profileLocalGames =>
      $$ProfileLocalGamesTableTableManager(_db, _db.profileLocalGames);
  $$ProfilePlayHistoriesTableTableManager get profilePlayHistories =>
      $$ProfilePlayHistoriesTableTableManager(_db, _db.profilePlayHistories);
  $$PlayActivitySyncPreferencesTableTableManager
  get playActivitySyncPreferences =>
      $$PlayActivitySyncPreferencesTableTableManager(
        _db,
        _db.playActivitySyncPreferences,
      );
  $$LocalPlaySessionsTableTableManager get localPlaySessions =>
      $$LocalPlaySessionsTableTableManager(_db, _db.localPlaySessions);
  $$PlayActivityOutboxTableTableManager get playActivityOutbox =>
      $$PlayActivityOutboxTableTableManager(_db, _db.playActivityOutbox);
  $$LegacyLocalInstallsTableTableManager get legacyLocalInstalls =>
      $$LegacyLocalInstallsTableTableManager(_db, _db.legacyLocalInstalls);
  $$LocalInstallsTableTableManager get localInstalls =>
      $$LocalInstallsTableTableManager(_db, _db.localInstalls);
  $$RuntimeOverrideRulesTableTableManager get runtimeOverrideRules =>
      $$RuntimeOverrideRulesTableTableManager(_db, _db.runtimeOverrideRules);
  $$ControllerBindingRulesTableTableManager get controllerBindingRules =>
      $$ControllerBindingRulesTableTableManager(
        _db,
        _db.controllerBindingRules,
      );
  $$ControllerMappingProfilesTableTableManager get controllerMappingProfiles =>
      $$ControllerMappingProfilesTableTableManager(
        _db,
        _db.controllerMappingProfiles,
      );
  $$ControllerProfileBindingRulesTableTableManager
  get controllerProfileBindingRules =>
      $$ControllerProfileBindingRulesTableTableManager(
        _db,
        _db.controllerProfileBindingRules,
      );
  $$ControllerPreferencesRowsTableTableManager get controllerPreferencesRows =>
      $$ControllerPreferencesRowsTableTableManager(
        _db,
        _db.controllerPreferencesRows,
      );
  $$ControllerHardwareMappingsTableTableManager
  get controllerHardwareMappings =>
      $$ControllerHardwareMappingsTableTableManager(
        _db,
        _db.controllerHardwareMappings,
      );
}
