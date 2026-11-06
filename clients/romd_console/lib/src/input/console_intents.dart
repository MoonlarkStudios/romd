import 'package:flutter/widgets.dart';

/// Open the preservation record / details for the current item.
///
/// The rest of the console's navigation reuses Flutter's built-in intents —
/// [DirectionalFocusIntent] (move), [ActivateIntent] (select), and
/// [DismissIntent] (back) — which the keyboard already maps to by default and
/// which the gamepad dispatches too. "Details" has no built-in intent, so this
/// is the one we define; the keyboard binds Y to it and the gamepad's Y button
/// dispatches it.
final class ShowDetailsIntent extends Intent {
  const ShowDetailsIntent();
}

/// Open the session Players surface from an eligible launcher route.
final class ShowPlayersIntent extends Intent {
  const ShowPlayersIntent();
}

/// Open the in-game controller reference for the current title. Bound to `C` on
/// the keyboard; mirrors [ShowDetailsIntent]'s no-built-in-intent pattern.
final class ShowControlsIntent extends Intent {
  const ShowControlsIntent();
}

/// Selects the previous section in a Discover surface.
///
/// The intent is intentionally route-agnostic. Only Discover installs an
/// action for it, so shoulder input on every other route remains a no-op.
final class PreviousCatalogSectionIntent extends Intent {
  const PreviousCatalogSectionIntent();
}

/// Selects the next section in a Discover surface.
///
/// Search may bind this contextually to its results zone while it is active.
final class NextCatalogSectionIntent extends Intent {
  const NextCatalogSectionIntent();
}
