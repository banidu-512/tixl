#nullable enable
using T3.Editor.SystemUi;
using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Interfaces;
using T3.Core.Operator.Slots;
using T3.Core.Output;
using T3.Core.Output.Rendering;
using T3.Core.Output.Streaming;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction.CanvasEditing;
using T3.Editor.Gui.InputUi.ListInputs;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.Commands.Graph;
using T3.Editor.UiModel.Commands.Setup;
using T3.Editor.UiModel.InputsAndTypes;
using T3.Editor.UiModel.ProjectHandling;
using T3.Editor.UiModel.Selection;

namespace T3.Editor.Gui.Windows.OutputSetup;

/// <summary>
/// Draws the selected setup entity's properties in the Parameter window — the successor of the setup
/// sidebar's properties footer. It draws only while the entity selection owns the inspection
/// (<see cref="GlobalSelectionHandling"/>); a graph pick takes the window back. Cards use the standard
/// <see cref="FormInputs"/> conventions; continuous fields commit gesture-scoped snapshot undo steps.
/// </summary>
internal static class SetupParameterView
{
    /// <summary>
    /// Draws the primary entity's card and returns true while the entity selection owns the inspection.
    /// Returns false — and lets the inspection go — when nothing resolves anymore (entity deleted, setup gone).
    /// </summary>
    public static bool TryDraw()
    {
        if (GlobalSelectionHandling.InspectionTarget != GlobalSelectionHandling.InspectionTargets.SetupEntity)
            return false;

        var selection = GlobalSelectionHandling.SetupEntities;
        if (!OutputSetupHandling.TryGetActiveSetup(out var setup, out var machineConfig)
            || !selection.TryResolve(setup, out var kind, out var id))
        {
            GlobalSelectionHandling.ReleaseInspection(GlobalSelectionHandling.InspectionTargets.SetupEntity);
            return false;
        }

        // The Parameter window disables its own scrolling, so long cards (a floor plan with many walls) need a child.
        ImGui.BeginChild("setupEntity", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        FormInputs.SetIndentToParameters();
        FormInputs.AddVerticalSpace(5);
        DrawHeader(setup, kind, id);

        // Entity cards can be far taller than the panel (a laser output: frame preview + scanner
        // camera + optimization + safety) and the parameter window deliberately has no scrollbar,
        // so the card body scrolls here. Header stays fixed.
        ImGui.BeginChild("##setupEntityCard", Vector2.Zero, ImGuiChildFlags.None);

        switch (kind)
        {
            case SetupEntityKinds.Surface:
                DrawSurfaceCard(setup, id);
                break;
            case SetupEntityKinds.Output:
                DrawOutputCard(setup, machineConfig, id);
                break;
            case SetupEntityKinds.ContentSource:
                DrawContentCard(setup, id);
                break;
            case SetupEntityKinds.Slice:
                DrawSliceCard(setup, id);
                break;
            case SetupEntityKinds.ReferenceImage:
                DrawReferenceImageCard(setup, id);
                break;
            case SetupEntityKinds.Prop:
                DrawPropCard(setup, id);
                break;
            case SetupEntityKinds.FloorPlan:
                DrawFloorPlanCard(setup, id);
                break;
            case SetupEntityKinds.Patch:
                DrawPatchCard(setup, id);
                break;
            case SetupEntityKinds.Plug:
                DrawPlugCard(setup, machineConfig, id);
                break;
        }

        if (selection.Count > 1)
        {
            FormInputs.AddVerticalSpace(8);
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText($"+{selection.Count - 1} more selected", Fonts.FontSmall, UiColors.TextMuted);
        }

        ImGui.EndChild();
        return true;
    }

    /// <summary>
    /// The setup side of a selected SendToOutput op, appended below its parameters: the content resolution
    /// and where its slices go. This is the one place op parameters and setup properties share a screen —
    /// deliberately, they describe the same thing.
    /// </summary>
    public static void DrawSendExtras(Instance instance)
    {
        if (instance is not IContentSupplier)
            return;

        if (!OutputSetupHandling.TryGetActiveSetup(out var setup, out _))
            return;

        FormInputs.SetIndentToParameters();
        FormInputs.AddSectionSubHeader("Output Setup");

        // Through the shared resolver: the same context and size the composite pulls with, so this shows the
        // frame the output renders rather than evaluating the graph a second time at a size of its own.
        OutputContentResolver.TryGetPreviewContent(instance.SymbolChildId, out var content);

        Span<int> resolution = [0, 0]; // nothing connected: no size, rather than a made-up one
        if (content is { IsDisposed: false })
        {
            resolution[0] = content.Description.Width;
            resolution[1] = content.Description.Height;
        }

        DrawIntsRow("Rendered (px)", resolution,
                    "What this content actually rendered at: the canvas of the output it is routed to, unless the op's Resolution is set.",
                    readOnly: true);

        // Two outputs of different sizes fed by one send: it renders once, so the second shows the first's size.
        if (OutputContentStats.TryGetSizeConflict(instance.SymbolChildId, out var conflictRendered, out var conflictOther))
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText($"⚠ also routed to an output of {conflictOther.Width}×{conflictOther.Height} — it shows {conflictRendered.Width}×{conflictRendered.Height}",
                                          Fonts.FontSmall, UiColors.StatusAttention);
        }

        var source = setup.FindSourceByChildId(instance.SymbolChildId);
        if (source != null)
        {
            var slices = SetupRelations.CountListedSlicesOfSource(setup, source.Id);
            var consumers = SetupRelations.CountConsumersOfSource(setup, source.Id);
            FormInputs.ApplyIndent();
            // The implicit full-frame slice reads as the source itself: "full frame", not "1 slice".
            var what = slices == 0 ? "full frame" : $"{slices} slice{(slices == 1 ? "" : "s")}";
            CustomComponents.StylizedText(consumers == 0
                                              ? $"{what}, nothing shows it yet"
                                              : $"{what} → {consumers} target{(consumers == 1 ? "" : "s")}",
                                          Fonts.FontSmall, UiColors.TextMuted);
        }
    }

    /// <summary>Installs the Guid-list parameter hooks so SendToOutput.TargetIds shows target names and a
    /// surface/output picker in the op parameter window. Called from UI registration at startup.</summary>
    public static void RegisterGuidListHooks()
    {
        GuidListLabels.Resolver = ResolveTargetLabel;
        GuidListLabels.Picker = PickTarget;
    }

    private static void DrawHeader(Setup setup, SetupEntityKinds kind, Guid id)
    {
        var kindInfo = SetupEntityKindInfo.Of(kind);
        // The two roles must never read alike: a plane-root is a Surface, a coplanar child is a Region.
        var kindLabel = kind == SetupEntityKinds.Surface && IsRegion(setup, id) ? "Region" : kindInfo.Label;

        FormInputs.ApplyIndent();
        Icons.DrawInlineGlyph(kindInfo.Icon, UiColors.TextMuted.Rgba);
        ImGui.SameLine(0, 6 * T3Ui.UiScaleFactor);
        CustomComponents.StylizedText(kindLabel, Fonts.FontLarge, UiColors.Text);
        FormInputs.AddVerticalSpace(4);

        // A display's name comes from the OS; a content source's is its op (rename cascades through the sync).
        var namedByOs = kind == SetupEntityKinds.Plug && !SetupActions.CanRenamePlug(id);
        if (kindInfo.CanRename && !namedByOs)
            DrawNameField(setup, kind, id);
    }

    /// <summary>Editable name, committed as one undoable rename when the field loses focus.</summary>
    private static void DrawNameField(Setup setup, SetupEntityKinds kind, Guid id)
    {
        var currentName = SetupLabels.NameForEntity(kind, id);
        if (_renameTargetId != id)
        {
            // The card moved on while a name was being typed: that name belongs to the entity it was typed for.
            if (_renameFieldActive && _renameTargetId != Guid.Empty && SetupEntities.Exists(setup, _renameTargetKind, _renameTargetId))
            {
                var typed = _renameBuffer.Trim();
                if (!string.IsNullOrWhiteSpace(typed) && typed != SetupLabels.NameForEntity(_renameTargetKind, _renameTargetId))
                    SetupActions.RenameEntity(setup, _renameTargetKind, _renameTargetId, typed);
            }

            _renameTargetId = id;
            _renameTargetKind = kind;
            _renameBuffer = currentName;
            _renameFieldActive = false;
        }
        else if (!_renameFieldActive)
        {
            _renameBuffer = currentName; // follow external renames while not editing
        }

        // The field is the entity's own item, so switching entities can never hand one's text to another.
        FormInputs.DrawInputLabel("Name");
        ImGui.PushID(id.GetHashCode());
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, fillWidth: true, maxWidth: FormInputs.MaxNumberInputWidth).X);
        ImGui.InputText("##entityName", ref _renameBuffer, 256);
        _renameFieldActive = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(_renameBuffer) && _renameBuffer.Trim() != currentName)
            SetupActions.RenameEntity(setup, kind, id, _renameBuffer.Trim());

        ImGui.PopID();
    }

    private static void DrawSurfaceCard(Setup setup, Guid id)
    {
        var surface = setup.FindSurface(id);
        if (surface == null)
            return;

        var render = surface.IsRendered;
        if (FormInputs.AddCheckBox("Render", ref render, "Skip drawing this surface without removing it."))
            SetupUndo.RunUndoable("Toggle render", setup, () => surface.IsRendered = render);

        var plan = setup.FindFloorPlanOf(surface.Id, out var planSegment);
        if (plan != null)
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText(planSegment < 0
                                              ? $"Floor of {plan.Name} — its size and place follow the plan's floor rectangle."
                                              : $"Wall on edge {planSegment + 1} of {plan.Name} — its width and place follow the plan.",
                                          Fonts.FontSmall, UiColors.TextMuted);
        }

        var position = surface.Placement?.Pose.Position ?? Vector3.Zero;
        Span<float> pos = [position.X, position.Y, position.Z];
        var posState = DrawFloatsRow("Position (m)", pos, readOnly: plan != null);
        BeginFieldUndo(setup, posState);
        if ((posState & InputEditStateFlags.Modified) != 0)
        {
            var placement = surface.Placement ??= new Surface.StagePlacement();
            placement.Pose = new Pose(new Vector3(pos[0], pos[1], pos[2]), placement.Pose.Orientation);
        }

        CommitFieldUndo(setup, "Move surface", posState);

        if (plan != null && planSegment >= 0)
        {
            Span<float> elevation = [plan.ElevationOf(surface.Id)];
            var elevationState = DrawFloatsRow("Elevation (m)", elevation,
                                               "How far the wall's bottom edge is lifted above the ground, for a wall the projection only covers from some height up. The wall keeps its height and rises as a whole.");
            BeginFieldUndo(setup, elevationState);
            if ((elevationState & InputEditStateFlags.Modified) != 0)
                FloorPlanSync.SetWallElevation(setup, plan, surface.Id, MathF.Max(elevation[0], 0));

            CommitFieldUndo(setup, "Change wall elevation", elevationState);
        }

        if (surface.Kind == Surface.Kinds.Physical)
            DrawStageOrientationRows(setup, surface, readOnly: plan != null);

        // A Layout child inherits its parent's plane, so it's placed in the parent's local space instead of the stage.
        if (surface.Kind == Surface.Kinds.Layout)
        {
            Span<float> local = [surface.LocalPosition.X, surface.LocalPosition.Y];
            var localState = DrawFloatsRow("Position in parent (m)", local,
                                           "Bottom-left corner, in metres from the parent's anchor (X right, Y up).");
            BeginFieldUndo(setup, localState);
            if ((localState & InputEditStateFlags.Modified) != 0)
                surface.LocalPosition = new Vector2(local[0], local[1]);

            CommitFieldUndo(setup, "Move region", localState);
        }

        Span<float> size = [surface.SizeInMeters.X, surface.SizeInMeters.Y];
        var sizeState = DrawFloatsRow("Size (m)", size,
                                      "How big the surface really is. Lines and regions scale with it; the projection and the trace stay where they are.",
                                      reserveRight: 44);

        // Locking keeps the current width/height ratio while resizing — the edited axis drives, the other follows.
        ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
        if (CustomComponents.IconButton(Icon.Link, Vector2.Zero,
                                        surface.IsAspectLocked ? CustomComponents.ButtonStates.Activated : CustomComponents.ButtonStates.Default))
        {
            SetupUndo.RunUndoable("Lock aspect", setup, () => surface.IsAspectLocked = !surface.IsAspectLocked);
        }

        CustomComponents.TooltipForLastItem("Lock aspect ratio", "Resizing keeps the current width-to-height ratio.");

        // Measuring is a different act from resizing: it states how big the rect you already aligned really
        // is, and must leave the projection alone. Explicit icon + Apply, rather than overloading the field.
        ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
        if (CustomComponents.IconButton(Icon.Scale, Vector2.Zero))
        {
            _measuredEdit = surface.SizeInMeters;
            ImGui.OpenPopup(MeasuredSizePopupId);
        }

        CustomComponents.TooltipForLastItem("Set measured dimensions",
                                            "Declares how big this surface really is, without moving the projection.");
        DrawMeasuredSizePopup(setup, surface);

        // A re-metering: lines and regions scale along, nothing moves on the wall. One undo step per gesture.
        BeginFieldUndo(setup, sizeState);
        if ((sizeState & InputEditStateFlags.Modified) != 0)
        {
            var typed = ConstrainSize(surface.SizeInMeters, new Vector2(size[0], size[1]), surface.IsAspectLocked);
            if (plan != null && planSegment >= 0)
            {
                // A wall's width is its segment: the typed number goes to the plan, which hands it back.
                SurfaceMetrics.RemeterSurface(setup, surface, new Vector2(surface.SizeInMeters.X, typed.Y));
                FloorPlanSync.SetSegmentLength(setup, plan, planSegment, typed.X);
            }
            else if (plan != null)
            {
                // The floor's size is its plan's floor rectangle.
                FloorPlanSync.SetFloorSize(setup, plan, typed);
            }
            else
            {
                SurfaceMetrics.RemeterSurface(setup, surface, typed);
            }
        }

        CommitFieldUndo(setup, "Resize surface", sizeState);

        var showGrid = surface.ShowGrid;
        if (FormInputs.AddCheckBox("Show size raster", ref showGrid,
                                   "Projects a real-world grid (no content needed) so you can hand-align the corner-pin to physical wall features."))
        {
            SetupUndo.RunUndoable("Toggle raster", setup, () => surface.ShowGrid = showGrid);
        }

        if (surface.ShowGrid)
        {
            Span<int> subdivisions = [surface.GridSubdivisions];
            var gridCellState = DrawIntsRow("Subdivisions / m", subdivisions,
                                            "Minor lines per metre; 1 draws metre lines only. They fade out once too dense to resolve.");
            BeginFieldUndo(setup, gridCellState);
            if ((gridCellState & InputEditStateFlags.Modified) != 0)
                surface.GridSubdivisions = Math.Clamp(subdivisions[0], 1, 100);

            CommitFieldUndo(setup, "Change raster", gridCellState);
        }

        DrawMappingModeRows(setup, surface);

        Span<float> anchor = [surface.Anchor.X, surface.Anchor.Y];
        var anchorState = DrawFloatsRow("Anchor (-1..1)", anchor,
                                        "Origin of the metre raster and of child regions: (0,0) is the centre, (0,-1) the bottom-centre, (±1,±1) the corners.");
        BeginFieldUndo(setup, anchorState);
        if ((anchorState & InputEditStateFlags.Modified) != 0)
            surface.Anchor = new Vector2(anchor[0], anchor[1]);

        CommitFieldUndo(setup, "Move anchor", anchorState);
    }

    /// <summary>
    /// One row per output this surface is shown on: whether it takes the whole canvas or sits on corners you
    /// place. A fill ignores every later resize of the surface, which is the point of it on a display; switching
    /// to a corner pin hands over the quad it filled, so nothing moves until a corner is dragged.
    /// </summary>
    private static void DrawMappingModeRows(Setup setup, Surface surface)
    {
        for (var i = 0; i < surface.OutputMappings.Count; i++)
        {
            var mapping = surface.OutputMappings[i];
            var output = setup.FindOutput(mapping.OutputId);
            if (output == null)
                continue;

            var mode = mapping.Mode;
            ImGui.PushID(mapping.OutputId.GetHashCode());
            if (FormInputs.AddSegmentedButtonWithLabel(ref mode, $"On {output.Name}",
                                                       "Fill takes the whole canvas and stays there however the surface is resized. "
                                                       + "Corner pin is aimed: you drag its corners onto the wall, and reshaping the surface on the wall moves it along."))
            {
                SetupUndo.RunUndoable("Change mapping mode", setup, () =>
                                                                    {
                                                                        if (mode == MappingModes.Fill)
                                                                            mapping.FillCanvas();
                                                                        else
                                                                            mapping.PromoteToCornerPin();
                                                                    });
            }

            ImGui.PopID();
        }
    }

    /// <summary>How a physical surface is turned in the stage: yaw, pitch and roll, for reading and fine-tuning a free surface.</summary>
    private static void DrawStageOrientationRows(Setup setup, Surface surface, bool readOnly)
    {
        var orientation = surface.Placement?.Pose.Orientation ?? System.Numerics.Quaternion.Identity;
        var degrees = StagePlacing.ToYawPitchRollDegrees(orientation);
        Span<float> rotation = [degrees.X, degrees.Y, degrees.Z];
        var rotationState = DrawFloatsRow("Rotation (°)", rotation,
                                          "Yaw, pitch and roll in the stage. An unturned surface stands upright facing the viewer; a floor is pitched -90.",
                                          speed: 0.5f, readOnly: readOnly, format: "{0:0.#}");
        BeginFieldUndo(setup, rotationState);
        if ((rotationState & InputEditStateFlags.Modified) != 0)
        {
            var placement = surface.Placement ??= new Surface.StagePlacement();
            placement.Pose = new Pose(placement.Pose.Position,
                                      StagePlacing.FromYawPitchRollDegrees(new System.Numerics.Vector3(rotation[0], rotation[1], rotation[2])));
        }

        CommitFieldUndo(setup, "Turn surface", rotationState);
    }

    /// <summary>
    /// A floor plan's card: whether it closes into a room and carries a floor, the height new walls get, and one
    /// row per edge with its length and a wall toggle — the venue's spec sheet, typed in as printed.
    /// </summary>
    private static void DrawFloorPlanCard(Setup setup, Guid id)
    {
        var plan = setup.FindFloorPlan(id);
        if (plan == null)
            return;

        FormInputs.ApplyIndent();
        plan.TryGetBounds(out var min, out var max);
        CustomComponents.StylizedText($"{max.X - min.X:0.##}×{max.Y - min.Y:0.##} m · drag its corners on the Board", Fonts.FontSmall, UiColors.TextMuted);

        var closed = plan.IsClosed;
        if (FormInputs.AddCheckBox("Closed", ref closed, "The last corner joins the first: a room, which can carry a floor."))
        {
            SetupUndo.RunUndoable("Toggle plan closed", setup, () =>
                                                              {
                                                                  plan.IsClosed = closed;
                                                                  plan.EnsureWallSlots();
                                                                  FloorPlanSync.Apply(setup, plan);
                                                              });
        }

        if (plan.IsClosed)
        {
            var hasFloor = setup.FindSurface(plan.RaisedFloorId) != null;
            if (FormInputs.AddCheckBox("Floor surface", ref hasFloor,
                                       "A surface lying on the footprint. Unticking removes it, unless content, a projection, regions or a patch of its name were put on it — then it is kept, free of the plan, and ticking again brings it back."))
                SetupUndo.RunUndoable(hasFloor ? "Add floor" : "Take up floor", setup, () => FloorPlanSync.SetFloor(setup, plan, hasFloor));

            if (hasFloor)
                DrawFloorRectRows(setup, plan);
        }

        Span<float> height = [plan.WallHeight];
        var heightState = DrawFloatsRow("Wall height (m)", height,
                                        "The height of this plan's walls. A wall given a height of its own keeps it, and a newly raised wall starts here.");
        BeginFieldUndo(setup, heightState);
        if ((heightState & InputEditStateFlags.Modified) != 0)
        {
            var previous = plan.WallHeight;
            plan.WallHeight = MathF.Max(height[0], 0.1f);
            FloorPlanSync.ApplyWallHeight(setup, plan, previous);
        }

        CommitFieldUndo(setup, "Change wall height", heightState);

        FormInputs.AddSectionSubHeader("Edges");
        for (var segment = 0; segment < plan.SegmentCount; segment++)
        {
            ImGui.PushID(segment);
            plan.GetSegment(segment, out var start, out var end);
            var wall = setup.FindSurface(plan.WallOf(segment));
            var lowered = wall == null ? setup.FindSurface(plan.SlotOf(segment)) : null;
            var hasWall = wall != null;
            var label = wall != null ? $"{wall.Name} · {(end - start).Length():0.##} m"
                        : lowered != null ? $"Edge {segment + 1} · {(end - start).Length():0.##} m · {lowered.Name} lowered"
                        : $"Edge {segment + 1} · {(end - start).Length():0.##} m";
            if (FormInputs.AddCheckBox(label, ref hasWall,
                                       "A wall standing on this edge, facing in. Unticking removes it, unless content, a projection, a trace, regions or a patch of its name were put on it — then it is kept, free of the plan, and ticking again brings it back."))
            {
                var index = segment;
                SetupUndo.RunUndoable(hasWall ? "Raise wall" : "Take down wall", setup, () => FloorPlanSync.SetWall(setup, plan, index, hasWall));
            }

            ImGui.PopID();
        }
    }

    /// <summary>
    /// The rectangle the floor surface and its patch stand for. It follows the footprint's bounding box until one of
    /// its fields is edited; a concave room usually wants it on the main hall, with a niche reaching past it.
    /// </summary>
    private static void DrawFloorRectRows(Setup setup, FloorPlan plan)
    {
        if (!plan.TryGetFloorRect(out var rect))
            return;

        var fitsBounds = plan.FloorRect == null;
        if (FormInputs.AddCheckBox("Floor fits bounds", ref fitsBounds,
                                   "The floor surface covers the footprint's bounding box. Editing the floor rectangle below, or its edges on the Board while the floor is selected, sets one of its own."))
        {
            SetupUndo.RunUndoable(fitsBounds ? "Fit floor to bounds" : "Keep floor rectangle", setup, () =>
                                                                                                    {
                                                                                                        plan.FloorRect = fitsBounds ? null : rect;
                                                                                                        FloorPlanSync.Apply(setup, plan);
                                                                                                    });
        }

        Span<float> centre = [rect.Centre.X, rect.Centre.Y];
        var centreState = DrawFloatsRow("Floor centre (m)", centre, "In plan metres from the stage origin, Y towards the front.");
        BeginFieldUndo(setup, centreState);
        if ((centreState & InputEditStateFlags.Modified) != 0)
        {
            rect.Centre = new Vector2(centre[0], centre[1]);
            plan.FloorRect = rect;
            FloorPlanSync.Apply(setup, plan);
        }

        CommitFieldUndo(setup, "Move floor", centreState);

        Span<float> size = [rect.Size.X, rect.Size.Y];
        var sizeState = DrawFloatsRow("Floor size (m)", size, "Width and depth of what the floor's patch shows, e.g. as printed on the pixel-map sheet.");
        BeginFieldUndo(setup, sizeState);
        if ((sizeState & InputEditStateFlags.Modified) != 0)
        {
            rect.Size = Vector2.Max(new Vector2(size[0], size[1]), new Vector2(SurfaceGeometry.MinSize));
            plan.FloorRect = rect;
            FloorPlanSync.Apply(setup, plan);
        }

        CommitFieldUndo(setup, "Resize floor", sizeState);

        Span<float> rotation = [rect.RotationDegrees];
        var rotationState = DrawFloatsRow("Floor rotation (°)", rotation, "Counter-clockwise seen from above, from the plan's X axis.", speed: 0.1f);
        BeginFieldUndo(setup, rotationState);
        if ((rotationState & InputEditStateFlags.Modified) != 0)
        {
            rect.RotationDegrees = rotation[0];
            plan.FloorRect = rect;
            FloorPlanSync.Apply(setup, plan);
        }

        CommitFieldUndo(setup, "Turn floor", rotationState);
    }

    private static void DrawOutputCard(Setup setup, MachineConfig machineConfig, Guid id)
    {
        var output = setup.FindOutput(id);
        if (output == null)
            return;

        if (output.Kind == OutputDefinition.Kinds.Laser)
        {
            DrawLaserOutputCard(setup, machineConfig, output);
            return;
        }

        var send = output.IsSending;
        if (FormInputs.AddCheckBox("Send", ref send, "Pause presenting without dropping the display binding."))
            SetupUndo.RunUndoable("Toggle send", setup, () => output.IsSending = send);

        // The canvas every route onto this output is measured in, and what its content is asked to render at:
        // an unset resolution upstream (a RenderTarget at 0×0) resolves to this, so it is the one place the
        // pixel size of a projector or a stream is decided.
        Span<int> canvas = [output.CanvasResolution.Width, output.CanvasResolution.Height];
        var canvasState = DrawIntsRow("Canvas (px)", canvas,
                                      "The output's pixel size, or 0 to take it from whatever is plugged in. Content rendering at 'Fill' follows it, and a stream sends at it.");
        BeginFieldUndo(setup, canvasState);
        if ((canvasState & InputEditStateFlags.Modified) != 0)
        {
            // 0 means "follow the plug". Only a render size either way: every quad on this canvas is stored as
            // a fraction of it, so changing it re-renders without moving a single mapping.
            output.CanvasResolution = OutputDefinition.ClampResolution(new T3.Core.DataTypes.Vector.Int2(canvas[0], canvas[1]));
        }

        CommitFieldUndo(setup, "Resize canvas", canvasState);

        var binding = machineConfig.FindBinding(output.Id);
        var boundTo = binding == null ? "unbound" : Plugs.BindingLabel(machineConfig, binding);
        FormInputs.ApplyIndent();
        CustomComponents.StylizedText(output.FollowsPlug
                                          ? $"following {boundTo} · {output.ResolvedResolution.Width}×{output.ResolvedResolution.Height}"
                                          : boundTo,
                                      Fonts.FontSmall, UiColors.TextMuted);

        DrawPixelMapRows(setup, output);
        DrawPatchTable(setup, output, Guid.Empty);
    }

    /// <summary>
    /// A laser output carries the show: the scanner camera that aims it and the processing pipeline
    /// (optimization + safety — managed here rather than in graph ops). The device itself — address,
    /// port, device settings — lives on the laser plug it is bound to; select the plug in LOCAL
    /// BINDINGS to edit it. Consumed by [LaserCamera]: setting its OutputRef to this output projects
    /// through the scanner camera, and the output's driver streams the resulting frame to the bound plug.
    /// </summary>
    private static void DrawLaserOutputCard(Setup setup, MachineConfig machineConfig, OutputDefinition output)
    {
        // A laser output may bind several plugs - the same frame fans out to each device.
        var boundPlugNames = new List<string>();
        foreach (var binding in machineConfig.FindLaserBindings(output.Id))
        {
            var plug = machineConfig.FindLaserPlug(binding.PlugId);
            if (plug != null)
                boundPlugNames.Add(plug.Name);
        }

        var boundTo = boundPlugNames.Count == 0
                          ? "unbound - drop this output on a laser plug (it can bind several)"
                          : $"→ {string.Join(", ", boundPlugNames)} (edit a plug under Local Bindings)";
        FormInputs.ApplyIndent();
        CustomComponents.StylizedText(boundTo, Fonts.FontSmall, UiColors.TextMuted);

        // The streamed frame, live — the same drawing the Board card shows, where the output is edited.
        FormInputs.DrawInputLabel("Laser frame");
        var previewHeight = 140f * T3Ui.UiScaleFactor;
        var previewMin = ImGui.GetCursorScreenPos();
        var previewMax = new Vector2(previewMin.X + MathF.Max(ImGui.GetContentRegionAvail().X, previewHeight), previewMin.Y + previewHeight);
        var previewDl = ImGui.GetWindowDrawList();
        previewDl.AddRectFilled(previewMin, previewMax, UiColors.BackgroundPopup.Fade(0.6f));
        LaserOutputPreview.DrawFrame(previewDl, previewMin, previewMax, LaserOutputPreview.GetFrame(output));
        ImGui.SetCursorScreenPos(new Vector2(previewMin.X, previewMax.Y + 4f * T3Ui.UiScaleFactor));
        ImGui.Dummy(Vector2.Zero);

        DrawLaserScannerCamera(setup, output);
        DrawLaserProcessing(setup, output);

        FormInputs.ApplyIndent();
        CustomComponents.StylizedText("Set a [LaserCamera]'s OutputRef to this output - it projects through the scanner camera above, "
                                      + "and while the master laser is on, the frame streams to the plug bound below.",
                                      Fonts.FontSmall, UiColors.TextMuted);
    }

    /// <summary>
    /// A laser plug's own card: the endpoint (address, port, network interface) and the device settings
    /// (scan rate, brightness ceiling, channel order, protocol identity) live HERE, at the plug in local
    /// bindings — the output bound here carries the show (camera, pipeline), never the device address.
    /// </summary>
    private static void DrawLaserPlugCard(MachineConfig machineConfig, LaserPlug plug, OutputDefinition? boundOutput)
    {
        // One control for the endpoint: type an address, or open the chevron to pick a discovered
        // device (Ether Dream DACs announce themselves by UDP broadcast).
        if (plug.Kind == LaserPlug.Kinds.EtherDream)
            LaserPlugDiscovery.EnsureEtherDreamDiscovery();
        else if (plug.Kind == LaserPlug.Kinds.Citp)
            CitpPeerDiscovery.EnsureStarted();
        else if (plug.Kind == LaserPlug.Kinds.Ponk)
            PonkSenderDiscovery.EnsureStarted();

        FormInputs.DrawInputLabel("Address");
        if (_laserAddressPlug != plug.Id)
        {
            _laserAddressPlug = plug.Id;
            _laserAddressBuffer = plug.Address;
        }

        var addressArrow = DrawEditableComboField("##laserAddress", ref _laserAddressBuffer, 64,
                                                  $"##laserAddressPick{plug.Id:N}");
        if (addressArrow)
            Plugs.UpdateLaser(machineConfig, plug.Id, null, _laserAddressBuffer.Trim(), null);

        CustomComponents.TooltipForLastItem(plug.Kind switch
                                                {
                                                    LaserPlug.Kinds.Ponk => "The PONK host: the 239.255.10.24 multicast group, or a receiver's unicast IP.",
                                                    LaserPlug.Kinds.Citp => "The CITP capture host (used with a Port above 0; 0 keeps PLoc multicast discovery).",
                                                    _ => "The Ether Dream DAC's IP address on the LAN — type it, or pick a discovered device from the chevron.",
                                                });

        if (plug.Kind == LaserPlug.Kinds.EtherDream && ImGui.BeginPopup($"##laserAddressPick{plug.Id:N}"))
        {
            var dacs = LaserPlugDiscovery.EtherDreamDacs;
            var connected = LaserPlugDiscovery.ConnectedDevice;

            if (dacs.Count == 0 && connected == null)
                CustomComponents.StylizedText("discovering…", Fonts.FontSmall, UiColors.TextMuted);

            foreach (var dac in dacs)
            {
                if (ImGui.Selectable($"{dac.Name} ({dac.Ip})") && dac.Ip != plug.Address)
                {
                    _laserAddressBuffer = dac.Ip;
                    Plugs.UpdateLaser(machineConfig, plug.Id, null, dac.Ip, null);
                    ImGui.CloseCurrentPopup();
                }
            }

            // A DAC being streamed to stops its discovery broadcasts — offer what an output op
            // is connected to, so the picker works during a show too.
            if (connected is { } device && dacs.All(d => !string.Equals(d.Ip, device.Ip, StringComparison.OrdinalIgnoreCase)))
            {
                if (ImGui.Selectable($"● connected: {device.Name} ({device.Ip})") && device.Ip != plug.Address)
                {
                    _laserAddressBuffer = device.Ip;
                    Plugs.UpdateLaser(machineConfig, plug.Id, null, device.Ip, null);
                    ImGui.CloseCurrentPopup();
                }
            }

            CustomComponents.StylizedText("DACs stop broadcasting while a show streams to them — the connected device is listed above.",
                                          Fonts.FontSmall, UiColors.TextMuted);
            ImGui.EndPopup();
        }
        else if (plug.Kind == LaserPlug.Kinds.Citp && ImGui.BeginPopup($"##laserAddressPick{plug.Id:N}"))
        {
            // CITP peers announce themselves (CITP PLoc on 224.0.0.180:4809) - Capture, Depence,
            // media servers. Picking one sets host and port for a direct connection.
            CitpPeerDiscovery.EnsureStarted();
            var peers = CitpPeerDiscovery.Peers;
            if (peers.Count == 0)
                CustomComponents.StylizedText("listening for CITP peers announcing themselves…", Fonts.FontSmall, UiColors.TextMuted);

            foreach (var peer in peers)
            {
                if (ImGui.Selectable($"{peer.Name} ({peer.Type}) — {peer.Ip}:{peer.TcpPort}")
                    && (peer.Ip != plug.Address || peer.TcpPort != plug.Port))
                {
                    _laserAddressBuffer = peer.Ip;
                    Plugs.UpdateLaser(machineConfig, plug.Id, null, peer.Ip, peer.TcpPort);
                    ImGui.CloseCurrentPopup();
                }
            }

            CustomComponents.StylizedText("Peers appear as they announce themselves on 224.0.0.180:4809.",
                                          Fonts.FontSmall, UiColors.TextMuted);
            ImGui.EndPopup();
        }
        else if (plug.Kind == LaserPlug.Kinds.Ponk && ImGui.BeginPopup($"##laserAddressPick{plug.Id:N}"))
        {
            PonkSenderDiscovery.EnsureStarted();

            // Multicast first - it reaches every receiver and needs no address at all.
            const string multicastAddress = "239.255.10.24";
            if (ImGui.Selectable($"Multicast group — {multicastAddress} (all receivers)")
                && plug.Address != multicastAddress)
            {
                _laserAddressBuffer = multicastAddress;
                Plugs.UpdateLaser(machineConfig, plug.Id, null, multicastAddress, null);
                ImGui.CloseCurrentPopup();
            }

            var senders = PonkSenderDiscovery.Senders;
            foreach (var sender in senders)
            {
                if (ImGui.Selectable($"{sender.Name} — {sender.Ip}") && sender.Ip != plug.Address)
                {
                    _laserAddressBuffer = sender.Ip;
                    Plugs.UpdateLaser(machineConfig, plug.Id, null, sender.Ip, null);
                    ImGui.CloseCurrentPopup();
                }
            }

            if (senders.Count == 0)
                CustomComponents.StylizedText("listening for PONK senders on 239.255.10.24:5583…", Fonts.FontSmall, UiColors.TextMuted);

            CustomComponents.StylizedText("Multicast reaches every receiver without an address; a receive-only MadMapper cannot announce itself, so only senders on the group are listed.",
                                          Fonts.FontSmall, UiColors.TextMuted);
            ImGui.EndPopup();
        }

        // Ether Dream DACs listen on the protocol-fixed port, so editing would be a no-op — shown read-only
        // at its effective value; PONK and CITP endpoints are configurable.
        if (plug.Kind == LaserPlug.Kinds.EtherDream)
        {
            Span<int> port = [plug.Port > 0 ? plug.Port : 7765];
            DrawIntsRow("Port", port, "The Ether Dream protocol port is fixed at 7765.", readOnly: true);
        }
        else
        {
            Span<int> port = [plug.Port];
            var portState = DrawIntsRow("Port", port, "Remote port; 0 = the protocol's default (PONK 5583, CITP discovery).");
            if ((portState & InputEditStateFlags.Modified) != 0)
                Plugs.UpdateLaser(machineConfig, plug.Id, null, null, port[0]);
        }

        FormInputs.DrawInputLabel("Interface");
        if (_laserTextKey != plug.Id + ":if")
        {
            _laserTextKey = plug.Id + ":if";
            _laserTextBuffer = plug.LocalAddress;
        }

        var interfaceArrow = DrawEditableComboField("##laserLocal", ref _laserTextBuffer, 40,
                                                    $"##laserInterfacePick{plug.Id:N}");
        if (interfaceArrow)
        {
            plug.LocalAddress = _laserTextBuffer.Trim();
            OutputSetupHandling.SaveActive();
        }

        CustomComponents.TooltipForLastItem("Network interface packets leave on (this machine's NIC IP); empty = any. Type it, or pick from the chevron.");

        if (ImGui.BeginPopup($"##laserInterfacePick{plug.Id:N}"))
        {
            foreach (var nic in LaserPlugDiscovery.InterfaceAddresses)
            {
                if (ImGui.Selectable($"{nic.Name}: {nic.Ip}") && nic.Ip != plug.LocalAddress)
                {
                    _laserTextBuffer = nic.Ip;
                    plug.LocalAddress = nic.Ip;
                    OutputSetupHandling.SaveActive();
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }

        DrawLaserDeviceSettings(machineConfig, plug);

        FormInputs.ApplyIndent();
        var boundLabel = boundOutput == null ? "nothing bound" : $"streaming for {boundOutput.Name}";
        CustomComponents.StylizedText($"{plug.Kind} · {boundLabel}", Fonts.FontSmall, UiColors.TextMuted);

        // Live device status from the driver streaming this plug's output — connection, buffer,
        // playback, e-stop — colored by severity, full text (incl. the pipeline suffix) on hover.
        if (boundOutput != null)
        {
            FormInputs.ApplyIndent();
            if (LaserDriver.TryGetStreamStatus(boundOutput.Id, out var opStatus, out var opLevel))
            {
                var statusColor = opLevel switch
                                      {
                                          IStatusProvider.StatusLevel.Success => UiColors.StatusOkay,
                                          IStatusProvider.StatusLevel.Warning or IStatusProvider.StatusLevel.Error => UiColors.StatusAttention,
                                          _ => UiColors.TextMuted,
                                      };
                CustomComponents.StylizedText($"● {opStatus}", Fonts.FontSmall, statusColor);
                CustomComponents.TooltipForLastItem("Live status of the driver streaming to this plug's device.");
            }
            else
            {
                CustomComponents.StylizedText("● not streaming — no laser stream for this plug yet (is the master laser on?)", Fonts.FontSmall, UiColors.TextMuted);
            }
        }
    }

    /// <summary>
    /// An editable combo: an input line with a chevron button at its right that opens popupId (the
    /// device/NIC list drawn by the caller). Typing commits on deactivate; returns true then, with
    /// the buffer holding the committed text.
    /// </summary>
    private static bool DrawEditableComboField(string fieldId, ref string buffer, uint maxLength, string? popupId)
    {
        var scale = T3Ui.UiScaleFactor;
        var gap = 4 * scale;
        var arrowWidth = popupId != null ? ImGui.GetFrameHeight() : 0f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - arrowWidth - gap);
        ImGui.InputText(fieldId, ref buffer, maxLength);
        var committed = ImGui.IsItemDeactivatedAfterEdit();

        if (popupId == null)
            return committed;

        ImGui.SameLine(0, gap);
        // IconButton pushes the icon's id internally — two chevrons on one card (address +
        // interface) would collide, so scope it under the unique popup id.
        ImGui.PushID(popupId);
        var chevronClicked = CustomComponents.IconButton(Icon.ChevronDown, Vector2.Zero);
        ImGui.PopID();
        if (chevronClicked)
            ImGui.OpenPopup(popupId);

        return committed;
    }

    /// <summary>Device settings of the plug — machine state, saved on change like the address.</summary>
    private static void DrawLaserDeviceSettings(MachineConfig machineConfig, LaserPlug plug)
    {
        FormInputs.AddSectionSubHeader("Device");

        if (plug.Kind == LaserPlug.Kinds.EtherDream)
        {
            var device = plug.EtherDreamSettings;
            Span<int> scanRate = [device.ScanRate];
            var scanState = DrawIntsRow("Scan rate (pps)", scanRate,
                                        "Points per second requested from the DAC; 0 = the output op's own input. Stream more points per second than this or the DAC buffer drains and playback halts.",
                                        readOnly: false, reserveRight: 22);
            if ((scanState & InputEditStateFlags.Modified) != 0)
            {
                device.ScanRate = Math.Clamp(scanRate[0], 0, 100000);
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetScanRate", "scan rate",
                         () =>
                             {
                                 device.ScanRate = 30000;
                                 OutputSetupHandling.SaveActive();
                             }, null);

            DrawLaserCeilingAndSwap(machineConfig, plug, ref device.MaxBrightness, ref device.SwapColorChannels);
        }
        else if (plug.Kind == LaserPlug.Kinds.Ponk)
        {
            var device = plug.PonkSettings;

            var multicast = device.UseMulticast;
            if (FormInputs.AddCheckBox("Multicast", ref multicast, "Send to the multicast group (how MadMapper discovers PONK senders); off sends unicast."))
            {
                device.UseMulticast = multicast;
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetMulticast", "multicast",
                         () =>
                             {
                                 device.UseMulticast = true;
                                 OutputSetupHandling.SaveActive();
                             }, null);

            FormInputs.DrawInputLabel("Sender name");
            if (_laserTextKey != plug.Id + ":sender")
            {
                _laserTextKey = plug.Id + ":sender";
                _laserTextBuffer = device.SenderName;
            }

            if (ImGui.InputText("##laserSender", ref _laserTextBuffer, 32))
            {
                // committed on deactivate
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                device.SenderName = _laserTextBuffer.Trim();
                OutputSetupHandling.SaveActive();
            }

            ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
            ImGui.PushID("ResetSenderName");
            if (CustomComponents.IconButton(Icon.Reset, Vector2.Zero))
            {
                device.SenderName = "TiXL";
                _laserTextBuffer = device.SenderName;
                OutputSetupHandling.SaveActive();
            }

            ImGui.PopID();
            CustomComponents.TooltipForLastItem("Reset sender name", "Restores the default value.");

            Span<float> maxSpeed = [device.MaxScanSpeed];
            var speedState = DrawFloatsRow("Scan speed (x)", maxSpeed, "Sent as MAXSPEED path metadata - the requested scanner-speed multiplier. 1.0 = neutral.",
                                           reserveRight: 22);
            if ((speedState & InputEditStateFlags.Modified) != 0)
            {
                device.MaxScanSpeed = MathF.Max(0.01f, maxSpeed[0]);
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetScanSpeed", "scan speed", () => device.MaxScanSpeed = 1f, null);

            var ponkKeepalive = device.Keepalive;
            if (FormInputs.AddCheckBox("Keepalive", ref ponkKeepalive,
                                       "Keeps streaming while the LASER master is off. A preview override for receivers that only visualize - PONK receivers can drive real scanners, so while this is off the master stays a hard kill."))
            {
                device.Keepalive = ponkKeepalive;
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetPonkKeepalive", "keepalive",
                         () =>
                             {
                                 device.Keepalive = false;
                                 OutputSetupHandling.SaveActive();
                             }, null);

            DrawLaserCeilingAndSwap(machineConfig, plug, ref device.MaxBrightness, ref device.SwapColorChannels);
        }
        else if (plug.Kind == LaserPlug.Kinds.Citp)
        {
            var device = plug.CitpSettings;

            FormInputs.DrawInputLabel("Feed name");
            if (_laserTextKey != plug.Id + ":feed")
            {
                _laserTextKey = plug.Id + ":feed";
                _laserTextBuffer = device.FeedName;
            }

            if (ImGui.InputText("##laserFeed", ref _laserTextBuffer, 32))
            {
                // committed on deactivate
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                device.FeedName = _laserTextBuffer.Trim();
                OutputSetupHandling.SaveActive();
            }

            ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
            ImGui.PushID("ResetFeedName");
            if (CustomComponents.IconButton(Icon.Reset, Vector2.Zero))
            {
                device.FeedName = "Laser Feed";
                _laserTextBuffer = device.FeedName;
                OutputSetupHandling.SaveActive();
            }

            ImGui.PopID();
            CustomComponents.TooltipForLastItem("Reset feed name", "Restores the default value.");

            Span<int> sourceKey = [(int)device.SourceKey];
            var keyState = DrawIntsRow("Source key", sourceKey, "CITP sender key; 0 = generate one at random per session.",
                                       reserveRight: 22);
            if ((keyState & InputEditStateFlags.Modified) != 0)
            {
                device.SourceKey = (uint)Math.Clamp(sourceKey[0], 0, int.MaxValue);
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetSourceKey", "source key", () => device.SourceKey = 0, null);

            var citpKeepalive = device.Keepalive;
            if (FormInputs.AddCheckBox("Keepalive", ref citpKeepalive,
                                       "Keeps the feed streaming while the LASER master is off. CITP is a previz path that never drives projectors, so this defaults to on - Capture keeps showing the show while the real beam is dark."))
            {
                device.Keepalive = citpKeepalive;
                OutputSetupHandling.SaveActive();
            }

            DrawRowReset("ResetCitpKeepalive", "keepalive",
                         () =>
                             {
                                 device.Keepalive = false;
                                 OutputSetupHandling.SaveActive();
                             }, null);

            DrawLaserCeilingAndSwap(machineConfig, plug, ref device.MaxBrightness, ref device.SwapColorChannels);
        }
    }

    /// <summary>The two device settings every laser protocol shares: the final brightness ceiling
    /// applied at encode (the non-bypassable audit) and the RGB/BGR channel order.</summary>
    private static void DrawLaserCeilingAndSwap(MachineConfig machineConfig, LaserPlug plug, ref float maxBrightness, ref bool swap)
    {
        Span<float> ceiling = [maxBrightness];
        var ceilingState = DrawFloatsRow("Brightness ceiling", ceiling, "Final intensity ceiling (0..1) applied to every point right before streaming - runs inside the device, so no upstream bug can exceed it.",
                                         reserveRight: 22);
        if ((ceilingState & InputEditStateFlags.Modified) != 0)
        {
            maxBrightness = Math.Clamp(ceiling[0], 0f, 1f);
            OutputSetupHandling.SaveActive();
        }

        // The resets flag through DrawRowReset and apply here: the values are ref parameters,
        // which lambdas must not capture.
        var ceilingReset = false;
        DrawRowReset("ResetCeiling", "brightness ceiling", () => ceilingReset = true, null);
        if (ceilingReset)
        {
            maxBrightness = 1f;
            OutputSetupHandling.SaveActive();
        }

        var swapChannels = swap;
        if (FormInputs.AddCheckBox("Swap color channels", ref swapChannels, "Some projectors expect the color channels as BGR - swaps R and B when on."))
        {
            swap = swapChannels;
            OutputSetupHandling.SaveActive();
        }

        var swapReset = false;
        DrawRowReset("ResetSwapChannels", "color channels", () => swapReset = true, null);
        if (swapReset)
        {
            swap = false;
            OutputSetupHandling.SaveActive();
        }
    }

    /// <summary>The scanner camera: how the laser frame is projected. Manual look-at until a
    /// calibration solve fills pose and lens; consumed by [LaserCamera] through this output's OutputRef.</summary>
    private static void DrawLaserScannerCamera(Setup setup, OutputDefinition output)
    {
        FormInputs.AddSectionSubHeader("Scanner Camera");
        var cam = output.Camera ??= new OutputDefinition.ProjectorCamera();

        Span<float> position = [cam.ManualPosition.X, cam.ManualPosition.Y, cam.ManualPosition.Z];
        var positionState = DrawFloatsRow("Position (m)", position, "Where the scanner sits, in stage metres (X right, Y up, Z towards the viewer).",
                                          reserveRight: 22);
        BeginFieldUndo(setup, positionState);
        if ((positionState & InputEditStateFlags.Modified) != 0)
            cam.ManualPosition = new Vector3(position[0], position[1], position[2]);

        CommitFieldUndo(setup, "Aim laser", positionState);
        DrawRowReset("ResetScannerPosition", "scanner position",
                     () => cam.ManualPosition = new System.Numerics.Vector3(0, 1, 3), setup);

        Span<float> target = [cam.ManualTarget.X, cam.ManualTarget.Y, cam.ManualTarget.Z];
        var targetState = DrawFloatsRow("Target (m)", target, "The stage point the beam centre hits.",
                                        reserveRight: 22);
        BeginFieldUndo(setup, targetState);
        if ((targetState & InputEditStateFlags.Modified) != 0)
            cam.ManualTarget = new Vector3(target[0], target[1], target[2]);

        CommitFieldUndo(setup, "Aim laser", targetState);
        DrawRowReset("ResetScannerTarget", "scanner target",
                     () => cam.ManualTarget = System.Numerics.Vector3.Zero, setup);

        Span<float> fov = [cam.ManualFovYDegrees];
        var fovState = DrawFloatsRow("Scan angle (°)", fov,
                                     "The scanner's full optical scan angle - the aperture the ILDA field covers. ±20° galvos read 40°.",
                                     speed: 0.1f, format: "{0:0.0}", reserveRight: 22);
        BeginFieldUndo(setup, fovState);
        if ((fovState & InputEditStateFlags.Modified) != 0)
            cam.ManualFovYDegrees = Math.Clamp(fov[0], 1f, 179f);

        CommitFieldUndo(setup, "Aim laser", fovState);
        DrawRowReset("ResetScannerFov", "scan angle", () => cam.ManualFovYDegrees = 45, setup);

        FormInputs.ApplyIndent();
        if (cam.Pose != null && cam.Lens != null)
        {
            CustomComponents.StylizedText($"Calibration solve active (residual {cam.ResidualPx:0.##} px) - it overrides the manual aim.",
                                          Fonts.FontSmall, UiColors.StatusOkay);
        }
        else
        {
            CustomComponents.StylizedText("Manual aim - [LaserCamera] with this output as OutputRef projects through this camera.",
                                          Fonts.FontSmall, UiColors.TextMuted);
        }
    }

    /// <summary>
    /// The setup-driven pipeline: optimization and safety for this laser output, applied by the output
    /// operator before streaming. Safety starts uncalibrated - only the alignment rectangle streams -
    /// and the app-bar laser button stays orange until every laser output is calibrated.
    /// </summary>
    private static void DrawLaserProcessing(Setup setup, OutputDefinition output)
    {
        var processing = output.LaserProcessing ??= new LaserProcessingSettings();
        var optimizer = processing.Optimizer;
        var safety = processing.Safety;

        FormInputs.AddSectionSubHeader("Optimization");

        var enableOptimization = optimizer.EnableOptimization;
        if (FormInputs.AddCheckBox("Reorder path", ref enableOptimization,
                                   "Greedy nearest-neighbour tour over the lit segments (reversing segments when closer). Off keeps the drawn order; blanking and corner dwelling always apply."))
        {
            SetupUndo.RunUndoable("Toggle laser optimization", setup, () => optimizer.EnableOptimization = enableOptimization);
        }

        DrawRowReset("ResetReorderPath", "reorder path", () => optimizer.EnableOptimization = true, setup);

        Span<float> jump = [optimizer.MaxJumpDistance];
        var jumpState = DrawFloatsRow("Max jump (units)", jump, "ILDA units per blanked-jump segment; the blank count between shapes scales with jump length.",
                                      reserveRight: 22);
        BeginFieldUndo(setup, jumpState);
        if ((jumpState & InputEditStateFlags.Modified) != 0)
            optimizer.MaxJumpDistance = MathF.Max(1f, jump[0]);
        CommitFieldUndo(setup, "Laser optimization", jumpState);
        DrawRowReset("ResetMaxJump", "max jump", () => optimizer.MaxJumpDistance = 5000f, setup);

        Span<int> blanking = [optimizer.BlankingDelayPoints];
        var blankingState = DrawIntsRow("Blanking points", blanking, "Maximum blanked points inserted before a shape start (0..100).",
                                        reserveRight: 22);
        BeginFieldUndo(setup, blankingState);
        if ((blankingState & InputEditStateFlags.Modified) != 0)
            optimizer.BlankingDelayPoints = Math.Clamp(blanking[0], 0, 100);
        CommitFieldUndo(setup, "Laser optimization", blankingState);
        DrawRowReset("ResetBlanking", "blanking points", () => optimizer.BlankingDelayPoints = 5, setup);

        Span<float> corner = [optimizer.CornerAngleThreshold];
        var cornerState = DrawFloatsRow("Corner angle (°)", corner, "Corners sharper than this get dwell anchors.",
                                        reserveRight: 22);
        BeginFieldUndo(setup, cornerState);
        if ((cornerState & InputEditStateFlags.Modified) != 0)
            optimizer.CornerAngleThreshold = MathF.Max(0f, corner[0]);
        CommitFieldUndo(setup, "Laser optimization", cornerState);
        DrawRowReset("ResetCornerAngle", "corner angle", () => optimizer.CornerAngleThreshold = 30f, setup);

        Span<int> anchors = [optimizer.CornerAnchorCount];
        var anchorsState = DrawIntsRow("Corner anchors", anchors, "Repeated points at a corner (1..10) - brighter, steadier corners.",
                                       reserveRight: 22);
        BeginFieldUndo(setup, anchorsState);
        if ((anchorsState & InputEditStateFlags.Modified) != 0)
            optimizer.CornerAnchorCount = Math.Clamp(anchors[0], 1, 10);
        CommitFieldUndo(setup, "Laser optimization", anchorsState);
        DrawRowReset("ResetCornerAnchors", "corner anchors", () => optimizer.CornerAnchorCount = 3, setup);

        FormInputs.AddSectionSubHeader("Safety");

        var calibrated = safety.Calibrated;
        if (FormInputs.AddCheckBox("Calibrated", ref calibrated,
                                   "While off, upstream content is ignored and only the alignment rectangle below streams - aim it, then switch on for content. The app-bar laser button stays orange until every laser output is calibrated."))
        {
            SetupUndo.RunUndoable("Toggle laser calibration", setup, () => safety.Calibrated = calibrated);
        }

        // Test screen: the aiming aid that works BEFORE calibration - rectangle + output name, so
        // the projector on the wall can be told apart from its siblings. Transient show state.
        FormInputs.ApplyIndent();
        var testScreen = LaserDriver.IsTestScreen(output.Id);
        if (FormInputs.AddCheckBox("Test screen", ref testScreen,
                                   "Streams the alignment rectangle with this output's name scanned inside it - regardless of Calibrated - so you can tell which projector is which. The frame preview shows it too. Master enable still gates the beam."))
        {
            LaserDriver.SetTestScreen(output.Id, testScreen);
        }

        if (!calibrated)
        {
            Span<float> setupRect =
            [
                safety.SetupCenterX, safety.SetupCenterY, safety.SetupWidth, safety.SetupHeight,
            ];
            var rectState = DrawFloatsRow("Setup rect (cx, cy, w, h)", setupRect,
                                          "The alignment rectangle in normalized field units: center -1..1, width/height 0..1 where 1 = the full scanner field. Start tiny at center and extend step by step until the rectangle fills the projection, then switch Calibrated on.",
                                          speed: 0.001f, format: "{0:0.000}", reserveRight: 22);
            BeginFieldUndo(setup, rectState);
            if ((rectState & InputEditStateFlags.Modified) != 0)
            {
                safety.SetupCenterX = Math.Clamp(setupRect[0], -1f, 1f);
                safety.SetupCenterY = Math.Clamp(setupRect[1], -1f, 1f);
                safety.SetupWidth = Math.Clamp(setupRect[2], 0.01f, 1f);
                safety.SetupHeight = Math.Clamp(setupRect[3], 0.01f, 1f);
            }

            CommitFieldUndo(setup, "Aim setup rectangle", rectState);
            DrawRowReset("ResetSetupRect", "setup rectangle",
                         () =>
                             {
                                 safety.SetupCenterX = 0f;
                                 safety.SetupCenterY = 0f;
                                 safety.SetupWidth = 0.04f;
                                 safety.SetupHeight = 0.04f;
                             }, setup);
        }

        var beamEnable = safety.BeamEnable;
        if (FormInputs.AddCheckBox("Beam enable", ref beamEnable, "Master beam switch for this output; off blanks every point but keeps the geometry."))
        {
            SetupUndo.RunUndoable("Toggle laser beam", setup, () => safety.BeamEnable = beamEnable);
        }

        Span<float> zone =
        [
            safety.SafeZoneCenterX, safety.SafeZoneCenterY, safety.SafeZoneWidth, safety.SafeZoneHeight,
        ];
        var zoneState = DrawFloatsRow("Safe zone (cx, cy, w, h)", zone,
                                      "The region the beam may enter, in ILDA units (full field 65534 wide). Points outside are clamped in.",
                                      reserveRight: 22);
        BeginFieldUndo(setup, zoneState);
        if ((zoneState & InputEditStateFlags.Modified) != 0)
        {
            safety.SafeZoneCenterX = zone[0];
            safety.SafeZoneCenterY = zone[1];
            safety.SafeZoneWidth = MathF.Max(0f, zone[2]);
            safety.SafeZoneHeight = MathF.Max(0f, zone[3]);
        }

        CommitFieldUndo(setup, "Laser safe zone", zoneState);
        DrawRowReset("ResetSafeZone", "safe zone",
                     () =>
                         {
                             safety.SafeZoneCenterX = 0f;
                             safety.SafeZoneCenterY = 0f;
                             safety.SafeZoneWidth = 65534f;
                             safety.SafeZoneHeight = 65534f;
                         }, setup);

        Span<float> limits =
        [
            safety.MaxStepPerPoint, safety.MaxStaticPoints, safety.MaxBrightness,
        ];
        var limitsState = DrawFloatsRow("Jump / dwell / bright", limits,
                                        "Max jump per point (ILDA units, subdivided), max points dwelling in one place before blanking, and the show-level brightness ceiling (the plug's device ceiling stays the final audit).",
                                        reserveRight: 22);
        BeginFieldUndo(setup, limitsState);
        if ((limitsState & InputEditStateFlags.Modified) != 0)
        {
            safety.MaxStepPerPoint = MathF.Max(1f, limits[0]);
            safety.MaxStaticPoints = Math.Clamp((int)limits[1], 1, 100000);
            safety.MaxBrightness = Math.Clamp(limits[2], 0f, 1f);
        }

        CommitFieldUndo(setup, "Laser safety limits", limitsState);
        DrawRowReset("ResetSafetyLimits", "safety limits",
                     () =>
                         {
                             safety.MaxStepPerPoint = 4000f;
                             safety.MaxStaticPoints = 64;
                             safety.MaxBrightness = 1f;
                         }, setup);
    }

    /// <summary>
    /// The image drawn over the canvas while placing patches — a venue's pixel map at the output's pixel size.
    /// Picking a file creates the setup's reference image for it, or repoints the one already linked.
    /// </summary>
    private static void DrawPixelMapRows(Setup setup, OutputDefinition output)
    {
        FormInputs.AddSectionSubHeader("Pixel Map");
        var image = setup.FindReferenceImage(output.ReferenceImageId);

        // An empty string, not null: the picker only draws its search field for a string it can edit.
        FormInputs.DrawInputLabel("Image");
        string? path = image?.FilePath ?? string.Empty;
        var pathState = FilePickingUi.DrawTypeAheadSearch(FileOperations.FilePickerTypes.File, SetupActions.ImageFileFilter, ref path);
        if ((pathState & InputEditStateFlags.Modified) != 0 && !string.IsNullOrEmpty(path) && path != image?.FilePath)
        {
            SetupUndo.RunUndoable("Pick pixel map", setup, () =>
                                                           {
                                                               if (image == null)
                                                               {
                                                                   image = new ReferenceImage
                                                                               {
                                                                                   Name = $"{output.Name} map",
                                                                                   Kind = ReferenceImage.Kinds.PixelMap,
                                                                                   FilePath = path,
                                                                               };
                                                                   setup.ReferenceImages.Add(image);
                                                                   output.ReferenceImageId = image.Id;
                                                               }
                                                               else
                                                               {
                                                                   image.FilePath = path;
                                                               }
                                                           });
        }

        if (image == null)
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText("None — pick the venue's pixel map to place patches against.", Fonts.FontSmall, UiColors.TextMuted);
            return;
        }

        ImGui.SameLine();
        if (CustomComponents.IconButton(Icon.Close, Vector2.Zero, CustomComponents.ButtonStates.Default))
            SetupUndo.RunUndoable("Remove pixel map", setup, () => output.ReferenceImageId = Guid.Empty);

        CustomComponents.TooltipForLastItem("Remove the pixel map from this output", "The image stays in the setup.");

        var opacity = output.ReferenceOpacity;
        var opacityState = FormInputs.AddFloatWithEditState("Opacity", ref opacity, 0, 1, 0.005f, clampMin: true, clampMax: true,
                                                            "How strongly the map shows over the output's content.", defaultValue: 0.5f);
        BeginFieldUndo(setup, opacityState);
        if ((opacityState & InputEditStateFlags.Modified) != 0)
            output.ReferenceOpacity = opacity;

        CommitFieldUndo(setup, "Change pixel map opacity", opacityState);

        // The map is laid out for one canvas size; a mismatch means every patch typed from it lands off.
        FormInputs.ApplyIndent();
        if (image.Width <= 0)
        {
            CustomComponents.StylizedText("Not loaded yet — it shows once this output is open.", Fonts.FontSmall, UiColors.TextMuted);
        }
        else if (image.Width == output.CanvasResolution.Width && image.Height == output.CanvasResolution.Height)
        {
            CustomComponents.StylizedText($"{image.Width}×{image.Height} px · matches the canvas", Fonts.FontSmall, UiColors.TextMuted);
        }
        else
        {
            CustomComponents.StylizedText($"{image.Width}×{image.Height} px · canvas is {output.CanvasResolution.Width}×{output.CanvasResolution.Height}",
                                          Fonts.FontSmall, UiColors.StatusAttention);
            ImGui.SameLine(0, 6 * T3Ui.UiScaleFactor);
            if (ImGui.SmallButton("Use as Canvas Size"))
            {
                SetupUndo.RunUndoable("Resize canvas to pixel map", setup,
                                      () => output.CanvasResolution =
                                                OutputDefinition.ClampResolution(new T3.Core.DataTypes.Vector.Int2(image.Width, image.Height)));
            }
        }
    }

    /// <summary>
    /// Every patch on the canvas as one row — name, top-left, size, rotation — so a venue's spec sheet can be
    /// typed in as it is printed. Clicking a name selects that patch; <paramref name="highlightedPatchId"/>
    /// is the row shown as current. Fitted and warped patches show their rect read-only, since their shape is
    /// decided elsewhere (the aspect rows, the corners on the canvas).
    /// </summary>
    private static void DrawPatchTable(Setup setup, OutputDefinition output, Guid highlightedPatchId)
    {
        FormInputs.AddSectionSubHeader("Patches");
        var selection = GlobalSelectionHandling.SetupEntities;

        // A patch card draws the same switch for its own rows above, so this one needs its own id scope.
        ImGui.PushID("patchTable");
        if (output.Patches.Count > 0)
            DrawUnitSwitch();

        // Flush with the window's left edge: the table is its own block, not a value in the parameter column.
        var scale = T3Ui.UiScaleFactor;
        var tableWidth = ImGui.GetContentRegionAvail().X - InputArea.ValueEditRightMargin;
        var tableScreenX = ImGui.GetCursorScreenPos().X;
        var highlightMin = Vector2.Zero;
        var highlightMax = Vector2.Zero;
        if (output.Patches.Count > 0
            && ImGui.BeginTable("patches", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX,
                                new Vector2(tableWidth, 0)))
        {
            ImGui.TableSetupColumn("Patch", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("W", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("H", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Turn", ImGuiTableColumnFlags.WidthStretch, 0.9f);

            ImGui.TableNextRow();
            for (var column = 0; column < _patchColumnLabels.Length; column++)
            {
                ImGui.TableSetColumnIndex(column);
                CustomComponents.StylizedText(_patchColumnLabels[column], Fonts.FontSmall, UiColors.TextMuted);
            }

            var canvas = output.CanvasSize;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3 * scale);
            for (var index = 0; index < output.Patches.Count; index++)
            {
                var patch = output.Patches[index];
                ImGui.PushID(index);
                ImGui.TableNextRow();
                var isCurrent = patch.Id == highlightedPatchId || selection.IsSelected(SetupEntityKinds.Patch, patch.Id);
                if (isCurrent)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiColors.BackgroundActive.Fade(0.12f));

                // The name cell is a plain click target; the row's selection shows as an outline around the whole
                // row, drawn after the table so no cell paints over it.
                ImGui.TableSetColumnIndex(0);
                var rowTop = ImGui.GetCursorScreenPos().Y;

                // The whole name cell is the click and drag area, not just the letters of the name.
                var cellPos = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton("##row", new Vector2(MathF.Max(ImGui.GetContentRegionAvail().X, 1), ImGui.GetFrameHeight()));
                if (ImGui.IsItemClicked())
                    selection.Select(SetupEntityKinds.Patch, patch.Id);

                // A double-click renames, in the Flow Outliner's row, where names are edited.
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    OutlinerItem.BeginRename(selection, SetupEntityKinds.Patch, patch.Id, patch.Name);

                var nameFont = isCurrent ? Fonts.FontBold : Fonts.FontNormal;
                ImGui.GetWindowDrawList().AddText(nameFont, nameFont.FontSize, cellPos + new Vector2(0, (ImGui.GetFrameHeight() - nameFont.FontSize) * 0.5f),
                                                  isCurrent ? UiColors.Text : UiColors.TextMuted, SetupLabels.PatchLabel(output, patch));

                // Dragging the name past a neighbour swaps the two: the list is the composite order (later patches
                // draw over earlier ones), and the ordinal names follow it. One undo step for the whole drag.
                if (ImGui.IsItemActive() && !ImGui.IsItemHovered())
                {
                    var towards = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y < 0 ? -1 : 1;
                    var swapWith = index + towards;
                    if (swapWith >= 0 && swapWith < output.Patches.Count)
                    {
                        _patchReorderOldJson ??= setup.ToJsonString();
                        (output.Patches[index], output.Patches[swapWith]) = (output.Patches[swapWith], output.Patches[index]);
                        ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
                    }
                }

                DrawPatchRectCells(setup, output, patch, canvas);
                if (isCurrent)
                {
                    highlightMin = new Vector2(tableScreenX, rowTop - 2 * scale);
                    highlightMax = new Vector2(tableScreenX + tableWidth, rowTop + ImGui.GetFrameHeight() + 2 * scale);
                }

                ImGui.PopID();
            }

            ImGui.PopStyleVar();
            ImGui.EndTable();
            if (_patchReorderOldJson != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                SetupUndo.CommitGesture(setup, "Reorder patches", _patchReorderOldJson);
                _patchReorderOldJson = null;
            }

            if (highlightMax.X > highlightMin.X)
                ImGui.GetWindowDrawList().AddRect(highlightMin, highlightMax, UiColors.Selection.Fade(0.6f), 3 * scale, ImDrawFlags.None, 1 * scale);
        }

        if (ImGui.Button("Add Patch"))
            SetupActions.AddPatch(selection, setup, output);

        CustomComponents.TooltipForLastItem("Adds a full-canvas patch to place by dragging on the output, or by typing its rect here.");
        ImGui.PopID();
    }

    /// <summary>The X, Y, W, H and turn cells of one table row, editable for a freely placed axis-aligned patch.</summary>
    private static void DrawPatchRectCells(Setup setup, OutputDefinition output, OutputDefinition.Patch patch, Vector2 canvas)
    {
        var quad = patch.Quad;
        if (quad.Length < 4)
            return;

        CanvasDraw.Bounds(quad, out var min, out var max);
        var cells = _patchCellScratch;
        cells[0] = ToUnit(min.X, canvas.X);
        cells[1] = ToUnit(min.Y, canvas.Y);
        cells[2] = ToUnit(max.X - min.X, canvas.X);
        cells[3] = ToUnit(max.Y - min.Y, canvas.Y);

        const float aligned = 0.0001f; // of the canvas
        var isAxisAligned = MathF.Abs(quad[0].Y - quad[1].Y) < aligned && MathF.Abs(quad[2].Y - quad[3].Y) < aligned
                            && MathF.Abs(quad[0].X - quad[3].X) < aligned && MathF.Abs(quad[1].X - quad[2].X) < aligned;
        var editable = !patch.IsFitted;
        var format = EditsInPixels ? "{0:0}" : "{0:0.000}";

        var state = InputEditStateFlags.Nothing;
        for (var cell = 0; cell < 4; cell++)
        {
            ImGui.TableSetColumnIndex(cell + 1);
            if (!editable)
            {
                CustomComponents.StylizedText(string.Format(format, cells[cell]), Fonts.FontSmall, UiColors.TextMuted);
                continue;
            }

            ImGui.PushID(cell);
            var value = cells[cell];
            var cellState = SingleValueEdit.Draw(ref value, new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()),
                                                 float.NegativeInfinity, float.PositiveInfinity, clampMin: false, clampMax: false,
                                                 scale: EditsInPixels ? 1f : 0.001f, format: format);
            cells[cell] = EditsInPixels ? MathF.Round(value) : value;
            state |= cellState;
            ImGui.PopID();
        }

        if (editable)
        {
            BeginFieldUndo(setup, state);
            if ((state & InputEditStateFlags.Modified) != 0)
            {
                var topLeft = new Vector2(FromUnit(cells[0], canvas.X), FromUnit(cells[1], canvas.Y));
                var size = new Vector2(MathF.Max(FromUnit(cells[2], canvas.X), 0.0001f), MathF.Max(FromUnit(cells[3], canvas.Y), 0.0001f));
                ApplyBoundsToQuad(quad, isAxisAligned, min, max, topLeft, size);
            }

            CommitFieldUndo(setup, "Edit patch rect", state);
        }

        ImGui.TableSetColumnIndex(5);

        var degrees = DegreesOfTurns(patch.QuarterTurns);
        var turnState = SingleValueEdit.Draw(ref degrees, new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()),
                                             -180, 180, clampMin: false, clampMax: false, scale: 1f, format: "{0:0}°");
        BeginFieldUndo(setup, turnState);
        if ((turnState & InputEditStateFlags.Modified) != 0)
            SetupActions.TurnPatch(output, patch, TurnsOfDegrees(degrees) - patch.QuarterTurns);

        CommitFieldUndo(setup, "Rotate patch", turnState);
    }

    /// <summary>
    /// Gives a quad new bounds: an axis-aligned one is rewritten as that exact rectangle (which also squares a
    /// sub-pixel skew), a warped one is moved and scaled about its old top-left, so its shape rides along.
    /// </summary>
    private static void ApplyBoundsToQuad(Vector2[] quad, bool isAxisAligned, Vector2 oldMin, Vector2 oldMax, Vector2 newMin, Vector2 newSize)
    {
        if (isAxisAligned)
        {
            quad[0] = newMin;
            quad[1] = new Vector2(newMin.X + newSize.X, newMin.Y);
            quad[2] = newMin + newSize;
            quad[3] = new Vector2(newMin.X, newMin.Y + newSize.Y);
            return;
        }

        var oldSize = Vector2.Max(oldMax - oldMin, new Vector2(0.0001f));
        var factor = newSize / oldSize;
        for (var c = 0; c < 4; c++)
            quad[c] = newMin + (quad[c] - oldMin) * factor;
    }

    private static readonly string[] _patchColumnLabels = ["Patch", "X", "Y", "W", "H", "Turn"];
    private static string? _patchReorderOldJson; // the setup before a row drag started reordering, until the button is released
    private static readonly float[] _patchCellScratch = new float[4];

    /// <summary>
    /// A plug: what this machine presents through. A display is read-only (the OS owns its name and mode); a
    /// stream carries the settings its kind honours — the host asks the provider which those are, so a kind
    /// that has no notion of frame rate or alpha simply doesn't offer them.
    /// </summary>
    private static void DrawPlugCard(Setup setup, MachineConfig machineConfig, Guid id)
    {
        var boundOutput = Plugs.TryGetBoundOutput(setup, machineConfig, id);
        var boundLabel = boundOutput == null ? "nothing bound" : $"presenting {boundOutput.Name}";

        if (Plugs.TryGetDisplayIndex(id, out var displayIndex))
        {
            var screens = EditorUi.Instance.AllScreens;
            Span<int> mode = [0, 0];
            if (displayIndex < screens.Count)
            {
                mode[0] = screens[displayIndex].Bounds.Width;
                mode[1] = screens[displayIndex].Bounds.Height;
            }

            DrawIntsRow("Resolution (px)", mode, "The display's current mode (read-only).", readOnly: true);
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText(boundLabel, Fonts.FontSmall, UiColors.TextMuted);
            return;
        }

        var laser = machineConfig.FindLaserPlug(id);
        if (laser != null)
        {
            DrawLaserPlugCard(machineConfig, laser, boundOutput);
            return;
        }

        var stream = machineConfig.FindStreamPlug(id);
        if (stream == null)
            return;

        var provider = OutputStreamRegistry.TryGetProvider(stream.Kind);
        var supported = provider?.Supported ?? OutputStreamOptions.None;

        // What the receivers get: the canvas of the output bound here. A stream has no size of its own, but this is
        // where one looks for it, so an edit writes through to that output instead of pointing elsewhere.
        Span<int> resolution = [0, 0];
        if (boundOutput == null)
        {
            DrawIntsRow("Sends at (px)", resolution, "Nothing is bound to this stream, so it sends nothing.", readOnly: true);
        }
        else
        {
            resolution[0] = boundOutput.ResolvedResolution.Width;
            resolution[1] = boundOutput.ResolvedResolution.Height;
            var resolutionState = DrawIntsRow("Sends at (px)", resolution,
                                              $"The canvas of \"{boundOutput.Name}\", the output bound here — changing it resizes that output.");
            BeginFieldUndo(setup, resolutionState);
            if ((resolutionState & InputEditStateFlags.Modified) != 0)
            {
                // A bound output sends at its canvas, so 0 ("follow the plug") would be circular here.
                boundOutput.CanvasResolution = OutputDefinition.ClampResolution(new T3.Core.DataTypes.Vector.Int2(Math.Max(resolution[0], 1),
                                                                                                                 Math.Max(resolution[1], 1)));
            }

            CommitFieldUndo(setup, "Resize canvas", resolutionState);
        }

        if ((supported & OutputStreamOptions.FrameRate) != 0)
        {
            var frameRate = stream.FrameRate;
            if (FormInputs.AddInt("Frame rate", ref frameRate, 1, 240, 1,
                                  "Frames per second sent to receivers. Rendered frames in between are skipped, so a rate the render loop divides evenly into (30 from 60) is sent evenly.", 60))
            {
                stream.FrameRate = Math.Clamp(frameRate, 1, 240);
                OutputSetupHandling.SaveActive();
            }
        }

        if ((supported & OutputStreamOptions.Alpha) != 0)
        {
            var alpha = stream.EnableAlpha;
            if (FormInputs.AddCheckBox("Send alpha", ref alpha, "Carry the alpha channel; off sends an opaque frame."))
            {
                stream.EnableAlpha = alpha;
                OutputSetupHandling.SaveActive();
            }
        }

        FormInputs.ApplyIndent();
        var kindLine = provider == null ? $"{stream.Kind} · package not loaded" : $"{stream.Kind} · {boundLabel}";
        CustomComponents.StylizedText(kindLine, Fonts.FontSmall, UiColors.TextMuted);

        // A refused frame is otherwise invisible: the output looks bound and sending while receivers get nothing.
        if (OutputPresentation.TryGetStreamError(id, out var streamError))
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText(streamError, Fonts.FontSmall, UiColors.StatusAttention);
        }
    }

    /// <summary>Edits one of the send op's inputs as a regular parameter change: undoable, and it dirties the symbol.</summary>
    private static void SetInputUndoable<T>(Instance instance, IInputSlot slot, T value)
    {
        var parent = instance.Parent;
        if (parent == null || slot.Input.Value.Clone() is not InputValue<T> newValue)
            return;

        newValue.Value = value;
        UndoRedoStack.AddAndExecute(new ChangeInputValueCommand(parent.Symbol, instance.SymbolChildId, slot.Input, newValue, instance));
    }

    private static void DrawContentCard(Setup setup, Guid childId)
    {
        var instance = ContentSourceSync.FindSendInstance(childId);
        if (instance is not IContentSupplier supplier)
            return;

        OutputContentResolver.TryGetPreviewContent(childId, out var content);
        var context = OutputContentResolver.Context;

        var update = supplier.GetUpdateEnabled(context);
        if (FormInputs.AddCheckBox("Update", ref update, "When off, freezes this content at its last frame."))
            SetInputUndoable(instance, supplier.UpdateInput, update);

        // 0×0 means "whatever the output asks for", so the content follows the projector or display it is routed
        // to; a set value pins it. The line underneath says what that resolves to right now.
        var requested = supplier.GetResolution(context);
        Span<int> resolution = [requested.Width, requested.Height];
        var state = DrawIntsRow("Render at (px)", resolution,
                                "0 follows the output this content is routed to. Set a size to render at it regardless.");
        if ((state & InputEditStateFlags.Modified) != 0)
        {
            SetInputUndoable(instance, supplier.ResolutionInput,
                             new T3.Core.DataTypes.Vector.Int2(Math.Clamp(resolution[0], 0, 16384),
                                                               Math.Clamp(resolution[1], 0, 16384)));
        }

        FormInputs.ApplyIndent();
        CustomComponents.StylizedText(content is { IsDisposed: false }
                                          ? $"rendering {content.Description.Width}×{content.Description.Height}"
                                          : "nothing rendered yet",
                                      Fonts.FontSmall, UiColors.TextMuted);

        // Two outputs of different sizes fed by one send: it renders once, so the second shows the first's size.
        if (OutputContentStats.TryGetSizeConflict(instance.SymbolChildId, out var rendered, out var other))
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText($"⚠ also routed to an output of {other.Width}×{other.Height} — it shows {rendered.Width}×{rendered.Height}",
                                          Fonts.FontSmall, UiColors.StatusAttention);
        }
    }

    private static void DrawSliceCard(Setup setup, Guid id)
    {
        var slice = setup.FindSlice(id);
        if (slice == null)
            return;

        // Pixels come from the source texture; without a live source there's nothing to measure against, so
        // fall back to the normalized rect.
        var source = setup.FindSource(slice.SourceId);
        var texW = 0;
        var texH = 0;
        if (source != null && OutputContentResolver.TryGetPreviewContent(source.SymbolChildId, out var content)
            && content is { IsDisposed: false })
        {
            texW = content.Description.Width;
            texH = content.Description.Height;
        }

        var uv = slice.UvRect;
        if (texW <= 0 || texH <= 0)
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText("Connect the source op to edit in pixels.", Fonts.FontSmall, UiColors.TextMuted);
            Span<float> posUv = [uv.X, uv.Y];
            DrawFloatsRow("Position (uv)", posUv, readOnly: true);
            Span<float> sizeUv = [uv.Z - uv.X, uv.W - uv.Y];
            DrawFloatsRow("Size (uv)", sizeUv, readOnly: true);
            return;
        }

        var widthUv = MathF.Max(uv.Z - uv.X, SurfaceGeometry.MinSliceSize);
        var heightUv = MathF.Max(uv.W - uv.Y, SurfaceGeometry.MinSliceSize);

        // A slice is a fraction of its source, shown in whichever unit is selected — pixels of that source
        // while cutting an atlas, ratios when the source's size is not the point.
        DrawUnitSwitch();

        Span<float> position = [ToUnit(uv.X, texW), ToUnit(uv.Y, texH)];
        var positionState = DrawRectRow("Position", position);
        BeginFieldUndo(setup, positionState);
        if ((positionState & InputEditStateFlags.Modified) != 0)
        {
            var nx = Math.Clamp(FromUnit(position[0], texW), 0f, 1f - widthUv);
            var ny = Math.Clamp(FromUnit(position[1], texH), 0f, 1f - heightUv);
            slice.SetUvRect(new Vector4(nx, ny, nx + widthUv, ny + heightUv));
        }

        CommitFieldUndo(setup, "Move slice", positionState);

        Span<float> size = [ToUnit(widthUv, texW), ToUnit(heightUv, texH)];
        var sizePxState = DrawRectRow("Size", size);
        BeginFieldUndo(setup, sizePxState);
        if ((sizePxState & InputEditStateFlags.Modified) != 0)
        {
            var nw = Math.Clamp(FromUnit(size[0], texW), SurfaceGeometry.MinSliceSize, 1f - uv.X);
            var nh = Math.Clamp(FromUnit(size[1], texH), SurfaceGeometry.MinSliceSize, 1f - uv.Y);
            slice.SetUvRect(new Vector4(uv.X, uv.Y, uv.X + nw, uv.Y + nh));
        }

        CommitFieldUndo(setup, "Resize slice", sizePxState);
    }

    /// <summary>
    /// A patch is a rectangle of output pixels fed by one slice. Axis-aligned patches edit as position + size;
    /// a warped quad (surface-less keystone) is shown by its corners until the canvas editor lands.
    /// </summary>
    private static void DrawPatchCard(Setup setup, Guid id)
    {
        var patch = setup.FindPatch(id, out var output);
        if (patch == null || output == null)
            return;

        DrawPatchRows(setup, output, patch);

        // Its siblings on the same canvas, with this one current — a venue's patches are placed as a set.
        DrawPatchTable(setup, output, patch.Id);
    }

    private static void DrawPatchRows(Setup setup, OutputDefinition output, OutputDefinition.Patch patch)
    {
        var slice = setup.FindSlice(patch.SliceId);
        FormInputs.ApplyIndent();
        CustomComponents.StylizedText(slice == null
                                          ? "Nothing routed yet — drop a slice or content onto this patch."
                                          : $"Shows {SetupLabels.SliceLabel(setup, slice)} on {output.Name}",
                                      Fonts.FontSmall, UiColors.TextMuted);

        // Ahead of the geometry, which a warped quad skips: turning the picture applies to any patch.
        DrawPatchRotationRow(setup, output, patch);
        if (DrawPatchScaleRows(setup, output, patch))
            return;

        if (patch.Quad.Length < 4)
            return;

        var quad = patch.Quad;
        const float aligned = 0.0001f; // of the canvas
        var isAxisAligned = MathF.Abs(quad[0].Y - quad[1].Y) < aligned && MathF.Abs(quad[2].Y - quad[3].Y) < aligned
                            && MathF.Abs(quad[0].X - quad[3].X) < aligned && MathF.Abs(quad[1].X - quad[2].X) < aligned;
        if (!isAxisAligned)
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText("Warped quad — its corners are on the output canvas; these rows move and scale its bounds.", Fonts.FontSmall, UiColors.TextMuted);
        }

        // Stored as ratios of the canvas; shown in whichever unit is selected. The round trip only touches what
        // was edited, so a field left alone keeps its stored value bit for bit.
        DrawUnitSwitch();
        var canvas = output.CanvasSize;

        // Measured as the bounding box, like the table and the label: a corner drag may leave the quad skewed by a
        // fraction of a pixel, and the three readouts must still agree. Typing here squares it again.
        CanvasDraw.Bounds(quad, out var quadMin, out var quadMax);
        Span<float> position = [ToUnit(quadMin.X, canvas.X), ToUnit(quadMin.Y, canvas.Y)];
        var positionState = DrawRectRow("Position", position, "Top-left corner on the output canvas.");
        BeginFieldUndo(setup, positionState);
        if ((positionState & InputEditStateFlags.Modified) != 0)
        {
            var delta = new Vector2(FromUnit(position[0], canvas.X), FromUnit(position[1], canvas.Y)) - quadMin;
            for (var i = 0; i < 4; i++)
                quad[i] += delta;
        }

        CommitFieldUndo(setup, "Move patch", positionState);

        var covered = quadMax - quadMin;
        Span<float> size = [ToUnit(covered.X, canvas.X), ToUnit(covered.Y, canvas.Y)];
        var sizeState = DrawRectRow("Size", size);
        BeginFieldUndo(setup, sizeState);
        if ((sizeState & InputEditStateFlags.Modified) != 0)
        {
            var newSize = new Vector2(MathF.Max(FromUnit(size[0], canvas.X), 0.0001f), MathF.Max(FromUnit(size[1], canvas.Y), 0.0001f));
            ApplyBoundsToQuad(quad, isAxisAligned, quadMin, quadMax, quadMin, newSize);
        }

        CommitFieldUndo(setup, "Resize patch", sizeState);
    }

    /// <summary>
    /// The scale mode and, for a fitted patch, its aspect pair and scale. Returns true for a fitted patch: its
    /// place and size are derived from these, so the free Position and Size rows below don't apply to it.
    /// Every change re-fits at once, inside its undo step, so the snapshot holds the quad that is drawn.
    /// </summary>
    private static bool DrawPatchScaleRows(Setup setup, OutputDefinition output, OutputDefinition.Patch patch)
    {
        var mode = patch.ScaleMode;
        if (FormInputs.AddSegmentedButtonWithLabel(ref mode, "Scale mode",
                                                   "Stretch places the patch freely, as a share of the canvas, so it stretches when the canvas changes aspect. "
                                                   + "Fit keeps the aspect ratio below and fits it into the canvas, centred."))
        {
            // Switching back to Stretch keeps the fitted rectangle as the starting point for free editing.
            SetupUndo.RunUndoable("Change patch scale mode", setup, () =>
                                                                    {
                                                                        patch.ScaleMode = mode;
                                                                        patch.TryFitQuad(output.CanvasSize);
                                                                    });
        }

        if (!patch.IsFitted)
            return false;

        Span<float> aspect = [patch.AspectRatio.X, patch.AspectRatio.Y];
        var aspectState = DrawFloatsRow("Aspect ratio", aspect,
                                        "Width : height of the picture, such as 16 : 9 or 2.39 : 1. Measured along the picture, so a quarter turn fits it sideways.");
        BeginFieldUndo(setup, aspectState);
        if ((aspectState & InputEditStateFlags.Modified) != 0)
        {
            patch.AspectRatio = new Vector2(MathF.Max(aspect[0], MinAspectComponent), MathF.Max(aspect[1], MinAspectComponent));
            patch.TryFitQuad(output.CanvasSize);
        }

        CommitFieldUndo(setup, "Change patch aspect", aspectState);

        Span<float> scalePercent = [patch.Scale * 100f];
        var scaleState = DrawFloatsRow("Scale", scalePercent,
                                       "Size as a share of the largest rectangle of this aspect that fits the canvas. 100% touches the canvas edges.",
                                       speed: 0.5f, format: "{0:0.#}%");
        BeginFieldUndo(setup, scaleState);
        if ((scaleState & InputEditStateFlags.Modified) != 0)
        {
            patch.Scale = Math.Clamp(scalePercent[0] / 100f, OutputDefinition.Patch.MinScale, OutputDefinition.Patch.MaxScale);
            patch.TryFitQuad(output.CanvasSize);
        }

        CommitFieldUndo(setup, "Scale patch", scaleState);

        if (patch.Quad.Length == 4)
        {
            var pixels = (patch.Quad[2] - patch.Quad[0]) * output.CanvasSize;
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText(FittedSizeLabel(pixels), Fonts.FontSmall, UiColors.TextMuted);
        }

        return true;
    }

    /// <summary>The fitted size line, rebuilt only when the pixel size changes: the card draws every frame.</summary>
    private static string FittedSizeLabel(Vector2 pixels)
    {
        var width = (int)MathF.Round(pixels.X);
        var height = (int)MathF.Round(pixels.Y);
        if (width != _fittedLabelWidth || height != _fittedLabelHeight)
        {
            _fittedLabelWidth = width;
            _fittedLabelHeight = height;
            _fittedLabel = $"{width} × {height} px on the canvas, centred";
        }

        return _fittedLabel;
    }

    /// <summary>
    /// The picture's turn inside the patch, as the same angle field every rotation input in TiXL uses — its
    /// rotate buttons included, stepping a quarter turn here. Shown in degrees, positive counter-clockwise as
    /// everywhere else; stored as clockwise quarter turns, because only a quarter turn is a pure reordering of
    /// the quad's corners. A dragged or typed angle snaps to the nearest one.
    /// </summary>
    private static void DrawPatchRotationRow(Setup setup, OutputDefinition output, OutputDefinition.Patch patch)
    {
        const string tooltip = "Turns the patch around its centre in quarter turns, its shape and its picture together — for a display or LED panel mounted on its side.";
        _rotationScratch[0] = DegreesOfTurns(patch.QuarterTurns);

        var size = BeginValuesRow("Rotation", tooltip, 1, false, 0, out _);
        var rightPadding = ImGui.GetContentRegionAvail().X - size.X - InputArea.ValueEditRightMargin;
        var state = VectorValueEdit.Draw(_rotationScratch, -180, 180, 1f, clampMin: false, clampMax: false,
                                         rightPadding, "{0:0}°", rotationStep: 90f);
        EndValuesRow(tooltip, false);

        BeginFieldUndo(setup, state);
        if ((state & InputEditStateFlags.Modified) != 0)
            SetupActions.TurnPatch(output, patch, TurnsOfDegrees(_rotationScratch[0]) - patch.QuarterTurns);

        CommitFieldUndo(setup, "Rotate patch", state);
    }

    /// <summary>Clockwise quarter turns → the counter-clockwise angle the field shows, folded into -90..180.</summary>
    private static float DegreesOfTurns(int quarterTurns) => OutputDefinition.Patch.NormalizeTurns(quarterTurns) switch
                                                                 {
                                                                     1 => -90f,
                                                                     2 => 180f,
                                                                     3 => 90f,
                                                                     _ => 0f,
                                                                 };

    /// <summary>Any angle → the nearest clockwise quarter-turn count.</summary>
    private static int TurnsOfDegrees(float degrees)
    {
        var counterClockwiseQuarters = (int)MathF.Round(degrees / 90f);
        return OutputDefinition.Patch.NormalizeTurns(-counterClockwiseQuarters);
    }

    private static readonly float[] _rotationScratch = new float[1];

    private const float MinAspectComponent = 0.01f;
    private static int _fittedLabelWidth = -1;
    private static int _fittedLabelHeight = -1;
    private static string _fittedLabel = string.Empty;

    private static void DrawReferenceImageCard(Setup setup, Guid id)
    {
        var image = setup.FindReferenceImage(id);
        if (image == null)
            return;

        // The project's image assets, through the same type-ahead picker the LoadImage op uses.
        FormInputs.DrawInputLabel("Image");
        string? path = image.FilePath;
        var pathState = FilePickingUi.DrawTypeAheadSearch(FileOperations.FilePickerTypes.File, SetupActions.ImageFileFilter, ref path);
        if ((pathState & InputEditStateFlags.Modified) != 0 && path != null && path != image.FilePath)
            SetupUndo.RunUndoable("Pick reference image", setup, () => image.FilePath = path);

        FormInputs.ApplyIndent();
        CustomComponents.StylizedText(image.Width > 0
                                          ? $"{image.Width}×{image.Height} px · double-click its card on the Board to trace and straighten"
                                          : "No image loaded yet — pick one above, or drop a photo onto the Board.",
                                      Fonts.FontSmall, UiColors.TextMuted);

        var locked = image.IsLocked;
        if (FormInputs.AddCheckBox("Locked", ref locked, "A backdrop: drawn beneath every card, never picked, grabbed or fenced."))
            SetupUndo.RunUndoable(locked ? "Lock image" : "Unlock image", setup, () => image.IsLocked = locked);

        var opacity = image.Opacity;
        var opacityState = FormInputs.AddFloatWithEditState("Opacity", ref opacity, 0, 1, 0.005f, clampMin: true, clampMax: true,
                                                            "How strongly the card shows the image on the Board.", defaultValue: 1f);
        BeginFieldUndo(setup, opacityState);
        if ((opacityState & InputEditStateFlags.Modified) != 0)
            image.Opacity = opacity;

        CommitFieldUndo(setup, "Change image opacity", opacityState);

        // The scale line's length re-derives the image's scale; the line itself is drawn with Set Scale on the Board.
        if (image.HasScaleLine)
        {
            Span<float> length = [image.ScaleLineMeters];
            var lengthState = DrawFloatsRow("Scale line (m)", length, "The real length of the line drawn on the image. The card's size follows it.");
            BeginFieldUndo(setup, lengthState);
            if ((lengthState & InputEditStateFlags.Modified) != 0)
            {
                image.ScaleLineMeters = MathF.Max(length[0], 0.001f);
                SetupOutputView.ApplyScaleLine(image);
            }

            CommitFieldUndo(setup, "Change scale line length", lengthState);
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText($"{image.MetersPerPixel * 1000:0.##} mm per pixel", Fonts.FontSmall, UiColors.TextMuted);
        }
        else
        {
            FormInputs.ApplyIndent();
            CustomComponents.StylizedText("No scale yet — Set Scale in the card's menu draws a line over a known length.", Fonts.FontSmall, UiColors.TextMuted);
        }
    }

    private static void DrawPropCard(Setup setup, Guid id)
    {
        var prop = setup.FindProp(id);
        if (prop == null)
            return;

        Span<float> height = [prop.HeightInMeters];
        var heightState = DrawFloatsRow("Height (m)", height);
        BeginFieldUndo(setup, heightState);
        if ((heightState & InputEditStateFlags.Modified) != 0)
            prop.HeightInMeters = MathF.Max(height[0], 0.1f);

        CommitFieldUndo(setup, "Resize prop", heightState);
    }

    /// <summary>
    /// Row of drag-edit float fields sharing one label — hairline gaps and a rounded frame so the
    /// components read as a single control. Width and label column follow <see cref="FormInputs"/>.
    /// </summary>
    private static InputEditStateFlags DrawFloatsRow(string label, Span<float> values, string? tooltip = null,
                                                     float speed = 0.01f, bool readOnly = false,
                                                     string format = "{0:0.###}", float reserveRight = 0)
    {
        var size = BeginValuesRow(label, tooltip, values.Length, readOnly, reserveRight, out var gap);
        var result = InputEditStateFlags.Nothing;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);

            ImGui.PushID(i);
            var v = values[i];
            result |= SingleValueEdit.Draw(ref v, size, scale: speed, format: format);
            values[i] = v;
            ImGui.PopID();
        }

        EndValuesRow(tooltip, readOnly);
        return result;
    }

    /// <summary>
    /// The unit the rect fields below read in. Quads and slice rects are stored as ratios of what they sit on,
    /// so pixels are a presentation: handy while aiming at a projector's raster, wrong once the canvas changes
    /// size. Offered wherever such a rect is edited, and shared by all of them.
    /// </summary>
    private static void DrawUnitSwitch()
    {
        var units = UserSettings.Config.OutputSetupEditUnits;
        if (FormInputs.AddSegmentedButtonWithLabel(ref units, "Edit in",
                                                   "Pixels of the canvas or source this rect sits on, or ratios of it. Stored as ratios either way."))
        {
            UserSettings.Config.OutputSetupEditUnits = units;
        }
    }

    private static bool EditsInPixels => UserSettings.Config.OutputSetupEditUnits == OutputSetupEditUnits.Pixels;

    /// <summary>A ratio shown in the current unit, against the size of what it sits on. Pixels are rounded: a
    /// typed 1200 stored as a ratio comes back as 1199.9999, which must read as 1200 again.</summary>
    private static float ToUnit(float ratio, float extent) => EditsInPixels ? MathF.Round(ratio * extent) : ratio;

    /// <summary>The inverse of <see cref="ToUnit"/>, back to the stored ratio.</summary>
    private static float FromUnit(float value, float extent) => EditsInPixels ? value / MathF.Max(extent, 1) : value;

    /// <summary>
    /// A rect field in the selected unit: whole pixels when editing in pixels, ratios with decimals otherwise.
    /// Kept apart from <see cref="DrawIntsRow"/> because a ratio has no meaningful integer form.
    /// </summary>
    private static InputEditStateFlags DrawRectRow(string label, Span<float> values, string? tooltip = null)
    {
        var suffix = EditsInPixels ? " (px)" : " (ratio)";
        var size = BeginValuesRow(label + suffix, tooltip, values.Length, false, 0, out var gap);
        var result = InputEditStateFlags.Nothing;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);

            ImGui.PushID(i);
            var v = values[i];
            result |= SingleValueEdit.Draw(ref v, size, float.NegativeInfinity, float.PositiveInfinity,
                                           clampMin: false, clampMax: false,
                                           scale: EditsInPixels ? 1f : 0.001f,
                                           format: EditsInPixels ? "{0:0}" : "{0:0.0000}");
            values[i] = EditsInPixels ? MathF.Round(v) : v;
            ImGui.PopID();
        }

        EndValuesRow(tooltip, false);
        return result;
    }

    /// <summary>Integer counterpart of <see cref="DrawFloatsRow"/>.</summary>
    private static InputEditStateFlags DrawIntsRow(string label, Span<int> values, string? tooltip = null, bool readOnly = false,
                                                   float reserveRight = 0)
    {
        var size = BeginValuesRow(label, tooltip, values.Length, readOnly, reserveRight, out var gap);
        var result = InputEditStateFlags.Nothing;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);

            ImGui.PushID(i);
            var v = values[i];
            result |= SingleValueEdit.Draw(ref v, size);
            values[i] = v;
            ImGui.PopID();
        }

        EndValuesRow(tooltip, readOnly);
        return result;
    }

    /// <summary>Label, ID scope, frame style and the per-component size shared by the value rows; pair with <see cref="EndValuesRow"/>.</summary>
    private static Vector2 BeginValuesRow(string label, string? tooltip, int count, bool readOnly, float reserveRight, out float gap)
    {
        FormInputs.DrawInputLabel(label);
        ImGui.PushID(label);
        var scale = T3Ui.UiScaleFactor;
        gap = 1 * scale;

        // Capped like FormInputs' own number fields so setup cards line up with op parameters in the same window.
        var total = FormInputs.GetAvailableInputSize(tooltip, false, fillWidth: true, maxWidth: FormInputs.MaxNumberInputWidth).X
                    - reserveRight * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3 * scale);
        if (readOnly)
            ImGui.BeginDisabled();

        return new Vector2((total - gap * (count - 1)) / count, ImGui.GetFrameHeight());
    }

    private static void EndValuesRow(string? tooltip, bool readOnly)
    {
        if (readOnly)
            ImGui.EndDisabled();

        ImGui.PopStyleVar();
        ImGui.PopID();
        FormInputs.AppendTooltip(tooltip);
    }

    /// <summary>
    /// Snapshot-based undo for the card's continuous drag-fields. Call Begin right after the widget with its
    /// state flags and BEFORE applying the Modified value; call Commit after applying. The pre-edit setup is
    /// captured on the gesture's first event and committed as one undo step + a single save when the edit
    /// finishes — a whole drag (or typed entry) is one step, with no file writes while dragging.
    /// </summary>
    private static void BeginFieldUndo(Setup setup, InputEditStateFlags state)
    {
        if ((state & InputEditStateFlags.Started) != 0)
        {
            _fieldEditOldJson = setup.ToJsonString();
        }
        else if ((state & InputEditStateFlags.Modified) != 0)
        {
            _fieldEditOldJson ??= setup.ToJsonString();
        }

        // The edit lands right after this and is saved only when the field is let go.
        if ((state & InputEditStateFlags.Modified) != 0)
            OutputSetupHandling.NotifyLiveEdit();
    }

    private static void CommitFieldUndo(Setup setup, string name, InputEditStateFlags state)
    {
        if ((state & InputEditStateFlags.Finished) == 0 || _fieldEditOldJson == null)
            return;

        SetupUndo.CommitGesture(setup, name, _fieldEditOldJson);
        _fieldEditOldJson = null;
    }

    /// <summary>
    /// A reset-to-default button appended to the row just drawn (give the row reserveRight: 22 so the
    /// icon fits). Undoable on setup cards; machine-side rows (plugs) apply and save immediately.
    /// </summary>
    private static void DrawRowReset(string rowId, string what, Action reset, Setup? undoSetup)
    {
        ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
        ImGui.PushID(rowId);
        var clicked = CustomComponents.IconButton(Icon.Reset, Vector2.Zero);
        ImGui.PopID();
        CustomComponents.TooltipForLastItem($"Reset {what}", "Restores the default value.");
        if (!clicked)
            return;

        if (undoSetup != null)
            SetupUndo.RunUndoable($"Reset {what}", undoSetup, reset);
        else
            reset();
    }

    /// <summary>
    /// "Set measured dimensions": states the surface's real size without touching its corner pins. Used after
    /// the rect is already aligned on the wall — you're correcting the measurement, not moving the projection.
    /// The declared size drives the calibration raster's density and the straighten hypothesis.
    /// </summary>
    private static void DrawMeasuredSizePopup(Setup setup, Surface surface)
    {
        ImGui.SetNextWindowSize(new Vector2(260 * T3Ui.UiScaleFactor, 0));
        if (!SetupPopup.Begin(MeasuredSizePopupId))
            return;

        ImGui.PushFont(Fonts.FontBold);
        ImGui.TextUnformatted("Set measured dimensions");
        ImGui.PopFont();

        CustomComponents.StylizedText("The projection stays put — this only records\nhow big the surface really is.",
                                      Fonts.FontSmall, UiColors.TextMuted);

        Span<float> measured = [_measuredEdit.X, _measuredEdit.Y];
        DrawFloatsRow("Width × Height (m)", measured);
        _measuredEdit = new Vector2(measured[0], measured[1]);

        FormInputs.AddVerticalSpace(4);
        if (ImGui.Button("Apply"))
        {
            SetupUndo.RunUndoable("Set measured size", setup, () => SurfaceMetrics.RemeterSurface(setup, surface, _measuredEdit));
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        SetupPopup.End();
    }

    /// <summary>
    /// Applies a typed size, optionally preserving the previous ratio: the axis that changed more drives, the
    /// other follows. Keeps the driven axis exact so the number the user typed is what lands.
    /// </summary>
    private static Vector2 ConstrainSize(Vector2 old, Vector2 typed, bool lockAspect)
    {
        if (!lockAspect || old.X <= 0 || old.Y <= 0)
            return typed;

        var dx = MathF.Abs(typed.X - old.X);
        var dy = MathF.Abs(typed.Y - old.Y);
        if (dx >= dy)
            return new Vector2(typed.X, typed.X * (old.Y / old.X));

        return new Vector2(typed.Y * (old.X / old.Y), typed.Y);
    }

    private static bool IsRegion(Setup setup, Guid id)
    {
        var surface = setup.FindSurface(id);
        return surface is { Kind: Surface.Kinds.Layout } && surface.ParentId != Guid.Empty;
    }

    /// <summary>Full-width dropdown for a SendToOutput target-id list item: lists the active setup's surfaces
    /// then outputs; picking one returns the new id. The row's ImGui ID stack keeps each item's popup distinct.</summary>
    private static bool PickTarget(Guid current, float width, out Guid picked)
    {
        picked = current;

        if (!OutputSetupHandling.TryGetActiveSetup(out var setup, out _))
        {
            ImGui.BeginDisabled();
            ImGui.Button("(no setup)", new Vector2(width, 0));
            ImGui.EndDisabled();
            return false;
        }

        // Stable "###" id so the button keeps its identity as its label changes.
        if (ImGui.Button(ResolveTargetLabel(current) + "###pickTarget", new Vector2(width, 0)))
            ImGui.OpenPopup("##pickTargetPopup");

        var changed = false;
        if (SetupPopup.Begin("##pickTargetPopup"))
        {
            for (var i = 0; i < setup.Surfaces.Count; i++)
            {
                var surface = setup.Surfaces[i];
                if (ImGui.Selectable($"{surface.Name}##s{i}", surface.Id == current))
                {
                    picked = surface.Id;
                    changed = true;
                }
            }

            if (setup.Surfaces.Count > 0 && setup.Outputs.Count > 0)
                ImGui.Separator();

            for (var i = 0; i < setup.Outputs.Count; i++)
            {
                var output = setup.Outputs[i];
                if (ImGui.Selectable($"{output.Name}##o{i}", output.Id == current))
                {
                    picked = output.Id;
                    changed = true;
                }
            }

            SetupPopup.End();
        }

        return changed;
    }

    /// <summary>Names a SendToOutput target id for the parameter-window Guid list: a surface's short label
    /// or an output's name; "(missing)" when it resolves to nothing in the active setup (e.g. a target whose
    /// surface was deleted).</summary>
    private static string ResolveTargetLabel(Guid id)
    {
        if (id == Guid.Empty)
            return "(none)";

        if (!OutputSetupHandling.TryGetActiveSetup(out var setup, out _))
            return id.ToString("D")[..8];

        var surface = setup.FindSurface(id);
        if (surface != null)
            return SetupLabels.SurfaceShortLabel(surface);

        var output = setup.FindOutput(id);
        if (output != null)
            return output.Name;

        return "(missing)";
    }

    private const string MeasuredSizePopupId = "##measuredSize";
    private static Vector2 _measuredEdit;

    // Pre-edit setup snapshot while a card drag-field gesture is live (see BeginFieldUndo/CommitFieldUndo).
    private static string? _fieldEditOldJson;


    // Name-field editing state: buffer follows the entity until the field takes focus.
    private static Guid _renameTargetId;
    private static SetupEntityKinds _renameTargetKind;
    private static string _renameBuffer = string.Empty;
    private static bool _renameFieldActive;

    // Laser-plug address editing: the buffer reloads when a different plug's field is drawn.
    private static Guid _laserAddressPlug;
    private static string _laserAddressBuffer = string.Empty;

    // Laser-plug text rows (interface, PONK sender, CITP feed): one buffer keyed by plug + purpose.
    private static string? _laserTextKey;
    private static string _laserTextBuffer = string.Empty;
}
