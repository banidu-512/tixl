#nullable enable
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.Commands.Graph;

namespace T3.Editor.Collab;

/// <summary>
/// Converts undoable editor commands into compact wire ops and applies received ops to the local
/// graph. Encoders read the command's own data via internal accessors added to the command classes;
/// decoders rebuild the equivalent command from GUIDs plus plain data and execute it directly.
///
/// Remote ops deliberately bypass <see cref="UndoRedoStack"/>: they call command.Do() without Add(),
/// so a remote edit neither enters the local undo history nor re-triggers the broadcast hook (the
/// hook lives in UndoRedoStack.Add). Apply runs on the UI thread only.
/// </summary>
internal static class CollabCommandWire
{
    private static readonly HashSet<string> _warnedOnce = [];

    /// <summary>Encodes a locally executed command into a wire op, or null when it can't be replicated.</summary>
    public static JObject? EncodeCommand(ICommand command)
    {
        try
        {
            return command switch
                       {
                           MacroCommand macro          => EncodeMacro(macro),
                           ChangeInputValueCommand iv  => EncodeInputValue(iv),
                           AddSymbolChildCommand ac    => EncodeAddChild(ac),
                           DeleteSymbolChildrenCommand dc => EncodeDeleteChildren(dc),
                           AddConnectionCommand aconn  => EncodeConnection("add_conn", aconn.ConnectionForCollab, aconn.CompositionSymbolIdForCollab, aconn.MultiInputIndexForCollab),
                           DeleteConnectionCommand dconn => EncodeConnection("del_conn", dconn.ConnectionForCollab, dconn.CompositionSymbolIdForCollab, dconn.MultiInputIndexForCollab),
                           ChangeSymbolChildNameCommand rn => EncodeRename(rn),
                           ModifyCanvasElementsCommand mv => EncodeMove(mv),
                           _                           => WarnUnencoded(command),
                       };
        }
        catch (Exception e)
        {
            // Encoding must never break the local edit that produced the command.
            Log.Warning($"Collab: failed to encode {command.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static JObject? WarnUnencoded(ICommand command)
    {
        var typeName = command.GetType().Name;
        if (_warnedOnce.Add(typeName))
            Log.Debug($"Collab: {typeName} has no wire encoder and is not synced");

        return null;
    }

    private static JObject? EncodeMacro(MacroCommand macro)
    {
        var ops = new JArray();
        foreach (var subCommand in macro.CommandsForCollab)
        {
            var encoded = EncodeCommand(subCommand);
            if (encoded != null)
                ops.Add(encoded);
        }

        if (ops.Count == 0)
            return null;

        return new JObject
                   {
                       ["type"] = "batch",
                       ["ops"] = ops,
                   };
    }

    private static JObject? EncodeInputValue(ChangeInputValueCommand command)
    {
        // Animated values replay as keyframe edits which need the receiving animator state to match;
        // out of scope for this pass. The plain value path covers the common parameter editing case.
        if (command.WasAnimatedForCollab)
            return WarnUnencoded(command);

        return new JObject
                   {
                       ["type"] = "set_input",
                       ["c"] = command.CompositionSymbolIdForCollab,
                       ["ch"] = command.ChildIdForCollab,
                       ["i"] = command.InputIdForCollab,
                       ["v"] = InputValueToJson(command.NewValueForCollab),
                   };
    }

    private static JObject EncodeAddChild(AddSymbolChildCommand command)
    {
        return new JObject
                   {
                       ["type"] = "add_child",
                       ["c"] = command.ParentSymbolIdForCollab,
                       ["sym"] = command.SymbolIdForCollab,
                       ["id"] = command.AddedChildId,
                       ["x"] = command.PosOnCanvas.X,
                       ["y"] = command.PosOnCanvas.Y,
                       ["w"] = command.Size.X,
                       ["h"] = command.Size.Y,
                       ["name"] = command.ChildName ?? "",
                   };
    }

    private static JObject EncodeDeleteChildren(DeleteSymbolChildrenCommand command)
    {
        var ids = new JArray();
        foreach (var childId in command.RemovedChildIdsForCollab)
            ids.Add(childId);

        return new JObject
                   {
                       ["type"] = "del_children",
                       ["c"] = command.CompositionSymbolIdForCollab,
                       ["ids"] = ids,
                   };
    }

    private static JObject EncodeConnection(string type, Symbol.Connection connection, Guid compositionId, int multiInputIndex)
    {
        return new JObject
                   {
                       ["type"] = type,
                       ["c"] = compositionId,
                       ["src"] = connection.SourceParentOrChildId,
                       ["ss"] = connection.SourceSlotId,
                       ["dst"] = connection.TargetParentOrChildId,
                       ["ds"] = connection.TargetSlotId,
                       ["idx"] = multiInputIndex,
                   };
    }

    private static JObject EncodeRename(ChangeSymbolChildNameCommand command)
    {
        return new JObject
                   {
                       ["type"] = "rename",
                       ["c"] = command.ParentSymbolIdForCollab,
                       ["ch"] = command.ChildIdForCollab,
                       ["name"] = command.NewName ?? "",
                   };
    }

    private static JObject EncodeMove(ModifyCanvasElementsCommand command)
    {
        var items = new JArray();
        foreach (var entry in command.EntriesForCollab)
        {
            items.Add(new JObject
                          {
                              ["id"] = entry.SelectableId,
                              ["x"] = entry.PosOnCanvas.X,
                              ["y"] = entry.PosOnCanvas.Y,
                              ["w"] = entry.Size.X,
                              ["h"] = entry.Size.Y,
                          });
        }

        return new JObject
                   {
                       ["type"] = "move",
                       ["c"] = command.CompositionSymbolIdForCollab,
                       ["items"] = items,
                   };
    }

    // ----------------------------------------------------------------------------------------
    // Decoding and applying
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a wire op to the local graph. Every op is individually guarded: a failed op logs and
    /// skips instead of desyncing or killing the session (e.g. when a client briefly misses an
    /// operator type another user just added).
    /// </summary>
    public static void ApplyOp(JObject op)
    {
        try
        {
            var type = CollabProtocol.GetString(op, "type");
            switch (type)
            {
                case "batch":
                    if (op["ops"] is JArray ops)
                    {
                        foreach (var child in ops)
                        {
                            if (child is JObject subOp)
                                ApplyOp(subOp);
                        }
                    }

                    break;

                case "set_input":
                    ApplySetInput(op);
                    break;

                case "add_child":
                    ApplyAddChild(op);
                    break;

                case "del_children":
                    ApplyDeleteChildren(op);
                    break;

                case "add_conn":
                    ApplyConnection(op, true);
                    break;

                case "del_conn":
                    ApplyConnection(op, false);
                    break;

                case "rename":
                    ApplyRename(op);
                    break;

                case "move":
                    ApplyMove(op);
                    break;

                default:
                    Log.Debug($"Collab: ignoring unknown op type '{type}'");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Collab: failed to apply op '{CollabProtocol.GetString(op, "type")}': {e.Message}");
        }
    }

    private static bool TryGetComposition(JObject op, out SymbolUi compositionUi)
    {
        compositionUi = null;
        return CollabProtocol.TryGetGuid(op, "c", out var compositionId)
               && SymbolUiRegistry.TryGetSymbolUi(compositionId, out compositionUi);
    }

    private static void ApplySetInput(JObject op)
    {
        if (!TryGetComposition(op, out var compositionUi)
            || !CollabProtocol.TryGetGuid(op, "ch", out var childId)
            || !CollabProtocol.TryGetGuid(op, "i", out var inputId)
            || op["v"] == null)
            return;

        if (!compositionUi.Symbol.Children.TryGetValue(childId, out var child)
            || !child.Inputs.TryGetValue(inputId, out var input))
            return;

        var valueToken = op["v"];
        if (valueToken.Type == JTokenType.Null)
            return;

        var newValue = input.Value.Clone();
        newValue.SetValueFromJson(valueToken);

        var command = new ChangeInputValueCommand(compositionUi.Symbol, childId, input, newValue);
        command.Do();
    }

    private static void ApplyAddChild(JObject op)
    {
        if (!TryGetComposition(op, out var compositionUi)
            || !CollabProtocol.TryGetGuid(op, "sym", out var symbolId)
            || !CollabProtocol.TryGetGuid(op, "id", out var childId)
            || !SymbolUiRegistry.TryGetSymbolUi(symbolId, out var symbolToAddUi))
            return;

        if (compositionUi.Symbol.Children.ContainsKey(childId))
            return; // Already added (e.g. replayed sync op).

        var x = op["x"]?.Value<float>() ?? 0f;
        var y = op["y"]?.Value<float>() ?? 0f;
        var w = op["w"]?.Value<float>() ?? SymbolUi.Child.DefaultOpSize.X;
        var h = op["h"]?.Value<float>() ?? SymbolUi.Child.DefaultOpSize.Y;

        var command = new AddSymbolChildCommand(compositionUi.Symbol, symbolId, childId)
                          {
                              PosOnCanvas = new Vector2(x, y),
                              Size = new Vector2(w, h),
                              ChildName = CollabProtocol.GetString(op, "name"),
                          };
        command.Do();
    }

    private static void ApplyDeleteChildren(JObject op)
    {
        if (!TryGetComposition(op, out var compositionUi) || op["ids"] is not JArray ids)
            return;

        var childrenToRemove = new List<SymbolUi.Child>();
        foreach (var token in ids)
        {
            if (!Guid.TryParse(token.Value<string>(), out var childId))
                continue;

            // A child may already be gone (client-side deletes race). Deleting the remainder is
            // still the correct outcome.
            if (compositionUi.ChildUis.TryGetValue(childId, out var childUi))
                childrenToRemove.Add(childUi);
        }

        if (childrenToRemove.Count == 0)
            return;

        var command = new DeleteSymbolChildrenCommand(compositionUi, childrenToRemove);
        command.Do();
    }

    private static void ApplyConnection(JObject op, bool add)
    {
        if (!TryGetComposition(op, out var compositionUi)
            || !CollabProtocol.TryGetGuid(op, "src", out var sourceId)
            || !CollabProtocol.TryGetGuid(op, "ss", out var sourceSlotId)
            || !CollabProtocol.TryGetGuid(op, "dst", out var targetId)
            || !CollabProtocol.TryGetGuid(op, "ds", out var targetSlotId))
            return;

        var multiInputIndex = op["idx"]?.Value<int>() ?? 0;
        var connection = new Symbol.Connection(sourceId, sourceSlotId, targetId, targetSlotId);

        if (add)
        {
            // Re-adding an existing connection is a no-op in Symbol.AddConnection's normal index
            // handling, but skip explicitly to keep the sync replay idempotent.
            if (compositionUi.Symbol.Connections.Any(c => c.SourceParentOrChildId == sourceId
                                                           && c.SourceSlotId == sourceSlotId
                                                           && c.TargetParentOrChildId == targetId
                                                           && c.TargetSlotId == targetSlotId))
                return;

            var command = new AddConnectionCommand(compositionUi.Symbol, connection, multiInputIndex);
            command.Do();
        }
        else
        {
            var command = new DeleteConnectionCommand(compositionUi.Symbol, connection, multiInputIndex);
            command.Do();
        }
    }

    private static void ApplyRename(JObject op)
    {
        if (!TryGetComposition(op, out var compositionUi)
            || !CollabProtocol.TryGetGuid(op, "ch", out var childId)
            || !compositionUi.ChildUis.TryGetValue(childId, out var childUi))
            return;

        var command = new ChangeSymbolChildNameCommand(childUi, compositionUi.Symbol)
                          {
                              NewName = CollabProtocol.GetString(op, "name"),
                          };
        command.Do();
    }

    private static void ApplyMove(JObject op)
    {
        if (!TryGetComposition(op, out var compositionUi) || op["items"] is not JArray items)
            return;

        var targets = new List<(Guid Id, Vector2 Pos, Vector2 Size)>();
        foreach (var token in items)
        {
            if (token is not JObject item)
                continue;

            if (!CollabProtocol.TryGetGuid(item, "id", out var id))
                continue;

            targets.Add((id,
                         new Vector2(item["x"]?.Value<float>() ?? 0, item["y"]?.Value<float>() ?? 0),
                         new Vector2(item["w"]?.Value<float>() ?? 100, item["h"]?.Value<float>() ?? 100)));
        }

        if (targets.Count == 0)
            return;

        var command = ModifyCanvasElementsCommand.CreateForCollab(compositionUi.Symbol.Id, targets);
        command.Do();
    }

    // ----------------------------------------------------------------------------------------
    // Value serialization helpers
    // ----------------------------------------------------------------------------------------

    private static string InputValueToJson(InputValue value)
    {
        if (value == null)
            return "null";

        using var stringWriter = new StringWriter();
        using (var jsonWriter = new JsonTextWriter(stringWriter))
        {
            value.ToJson(jsonWriter);
        }

        return stringWriter.ToString();
    }
}
