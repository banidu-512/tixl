#nullable enable
using Newtonsoft.Json;
using T3.Core.Animation;
using T3.Core.Resource.Assets;
using T3.Core.Utils;
using T3.Serialization;
using T3.SystemUi;

// ReSharper disable RedundantNameQualifier
// ReSharper disable once InconsistentNaming

namespace Lib.point._experimental;

[Guid("b238b288-6e9b-4b91-bac9-3d7566416028")]
internal sealed class _SketchImpl : Instance<_SketchImpl>
{
    [Output(Guid = "EB2272B3-8B4A-46B1-A193-8B10BDC2B038")]
    public readonly Slot<object> OutPages = new();

    [Output(Guid = "974F46E5-B1DC-40AE-AC28-BBB1FB032EFE")]
    public readonly Slot<Vector3> CursorPosInWorld = new();

    [Output(Guid = "532B35D1-4FEE-41E6-AA6A-D42152DCE4A0")]
    public readonly Slot<float> CurrentBrushSize = new();

    [Output(Guid = "E1B35EFA-3A49-4AB3-83AE-A2DED1CEF908")]
    public readonly Slot<int> ActivePageIndexOutput = new();

    [Output(Guid = "BD29C7D2-1296-48CB-AD85-F96C27A35B92")]
    public readonly Slot<string> StatusMessage = new();

    public _SketchImpl()
    {
        OutPages.UpdateAction += Update;
        CursorPosInWorld.UpdateAction += Update;
        StatusMessage.UpdateAction += Update;

        _keyframeSync = new(this);
    }

    private string GetAbsolutePath(string relativePath)
    {
        var sketchInstance = Parent;
        var compositionWithSketchOp = sketchInstance?.Parent;

        if (sketchInstance == null || compositionWithSketchOp == null)
            return relativePath;

        AssetRegistry.TryResolveAddress(relativePath, compositionWithSketchOp, out var path2, out _, isFolder: false, logWarnings: true);
        return path2;
    }

    private string _absolutePath = string.Empty;
    private int _overridePageIndex;

    private ColorModes _colorMode = ColorModes.Page;

    private bool _enableKeyframeSync;

    private void Update(EvaluationContext context)
    {
        var isFilePathDirty = FilePath.DirtyFlag.IsDirty;

        var overrideIndexWasDirty = OverridePageIndex.DirtyFlag.IsDirty;
        _overridePageIndex = OverridePageIndex.GetValue(context);

        if (this.Parent == null)
        {
            Log.Warning("Implementation needs a wrapper op", this);
            return;
        }

        AssignUniqueFilePath();

        if (isFilePathDirty)
        {
            var filepath = FilePath.GetValue(context) ?? string.Empty;
            _absolutePath = GetAbsolutePath(filepath);
            //Log.Debug($"Absolute path: {_absolutePath}", this);
            _paging.LoadPages(_absolutePath);
        }

        var pageIndexNeedsUpdate = Math.Abs(_lastUpdateContextTime - context.LocalTime) > 0.001;
        if (pageIndexNeedsUpdate || isFilePathDirty || overrideIndexWasDirty)
        {
            _paging.UpdatePageIndex(context.LocalTime, _overridePageIndex);
            _lastUpdateContextTime = context.LocalTime;
        }

        _enableKeyframeSync = EnableKeyframeSync.GetValue(context);
        if (_enableKeyframeSync)
        {
            _keyframeSync.UpdateIfEnabled(_enableKeyframeSync, context);
        }

        // Switch Brush size
        {
            if (StrokeSize.DirtyFlag.IsDirty)
            {
                _brushSize = StrokeSize.GetValue(context);
            }

            for (var index = 0; index < _numberKeys.Length; index++)
            {
                if (!KeyHandler.PressedKeys[_numberKeys[index]])
                    continue;

                _brushSize = (index * index + 0.5f) * 0.1f;
            }
        }

        // Switch colors
        var colorMode = ColorMode.GetEnumValue<ColorModes>(context);
        var isColorDirty = StrokeColor.IsDirty;
        var color = StrokeColor.GetValue(context);
        var colorNeedsUpdate = colorMode != _colorMode || isColorDirty;
        if (colorNeedsUpdate)
        {
            _colorMode = colorMode;
            if (colorMode == ColorModes.Page)
            {
                ColorizePage(color);
            }
        }

        // Switch modes
        if (IsOpSelected && !KeyHandler.PressedKeys[(int)Key.CtrlKey])
        {
            // if (Mode.DirtyFlag.IsDirty)
            // {
            //     _drawMode = (DrawModes)Mode.GetValue(context).Clamp(0, Enum.GetNames(typeof(DrawModes)).Length - 1);
            // }
            //
            if (KeyHandler.PressedKeys[(int)Key.P])
            {
                _drawMode = DrawModes.Draw;
                ClearSelection();
                _sketchChangeCount++;
            }
            else if (KeyHandler.PressedKeys[(int)Key.E])
            {
                EraseSelection();
                _drawMode = DrawModes.Erase;
            }
            else if (KeyHandler.PressedKeys[(int)Key.X])
            {
                _paging.Cut(_overridePageIndex);
                _sketchChangeCount++;
            }
            else if (KeyHandler.PressedKeys[(int)Key.V])
            {
                _paging.Paste(context.LocalTime, _overridePageIndex);
                _sketchChangeCount++;
            }
            else if (KeyHandler.PressedKeys[(int)Key.C])
            {
                ClearSelection();
            }
            else if (KeyHandler.PressedKeys[(int)Key.S])
            {
                _drawMode = DrawModes.Select;
            }
        }

        var wasModified = DoSketch(context, out CursorPosInWorld.Value, out CurrentBrushSize.Value);

        OutPages.Value = _paging.Pages;
        ActivePageIndexOutput.Value = _paging.ActivePageIndex;
        var pageTitle = _paging.HasActivePage ? $"PAGE{_paging.ActivePageIndex}" : "EMPTY PAGE";
        var tool = !IsOpSelected
                       ? "Not selected"
                       : _drawMode == DrawModes.Draw
                           ? "PEN"
                           : "ERASE";

        var cutSomething = _paging.HasCutPage ? "/ PASTE WITH V" : "";
        StatusMessage.Value = $"{pageTitle}: {tool} {cutSomething}";

        if (wasModified)
        {
            _lastModificationTime = Playback.RunTimeInSecs;
            _sketchChangeCount++;
        }

        var needsSave = _lastSavedSketchVersion < _sketchChangeCount;

        if (needsSave && Playback.RunTimeInSecs - _lastModificationTime > 2)
        {
            //var filepath1 = FilePath.GetValue(context);
            var folder = Path.GetDirectoryName(_absolutePath);
            if (string.IsNullOrEmpty(folder))
            {
                Log.Warning("No directory for sketch?", this);
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception e)
            {
                Log.Warning($"Can't create sketch directory {folder}? (${e.Message}", this);
                return;
            }

            JsonUtils.TrySaveJson(_paging.Pages, _absolutePath);
            _lastSavedSketchVersion = _sketchChangeCount;
        }
    }

    #region keyframe syncing ------------------------------------------
    /// <summary>
    /// Assumptions:
    /// - Keyframe values are always incremental without gaps
    /// 
    /// 
    /// Use-cases:
    /// - Curve changes:
    ///     - keyframes moved (no gaps, all pages indices present) -> update pages times. Then update order.
    ///     - keyframes removed (gaps) -> remove page(s) with missing index (sadly, there is no undo)
    ///     - keyframes added (two or more identical values) -> insert blank pages
    ///     - parameter no longer animated -> disable sync?
    ///
    ///  - page inserted
    ///     - insert keyframe at current time, update all page indices
    ///     - page disabled
    /// 
    /// </summary>
    // private void SyncWithKeyframes()
    // {
    //     var composition = Parent?.Parent;
    //     if (composition == null)
    //         return;
    //
    //     if (!composition.Symbol.Animator.TryGetCurvesForChildInput(Parent.SymbolChildId, _overrideKeyframeIndexInputId, out var animCurves))
    //         return;
    //
    //     if (animCurves.Length != 1)
    //         return;
    //
    //     var curve = animCurves[0];
    //     // if (curve.ChangeCount > _lastAnimCurveVersion)
    //     // {
    //     //     Log.Debug("Curve updated", this.Parent);
    //     //     _lastAnimCurveVersion = curve.ChangeCount;
    //     //
    //     //
    //     //     foreach (var key in curve.Keys)
    //     //     {
    //     //         
    //     //     }
    //     // }
    //     
    //
    //     // 1) If pages changed via sketching, reflect that as keys (authoring)
    //     //    Use your own “sketch changed” marker. This is the simplest:
    //     var pagesChanged = _sketchChangeCount != _lastSyncedSketchVersion;
    //     if (pagesChanged)
    //     {
    //         // Only do authoring when pages count/time changed in a way that implies insertion/removal.
    //         // The most important case: a new page appeared at current time.
    //         if (TryPushPageChangesToCurve(curve))
    //         {
    //             _lastSyncedSketchVersion = _sketchChangeCount;
    //             _ignoreNextCurveChange = true;
    //             _lastAnimCurveVersion = curve.ChangeCount; // optimistic; your curve may increment after edits
    //             return;
    //         }
    //     }
    //
    //     // 2) If curve changed by the user, apply to pages
    //     if (_ignoreNextCurveChange)
    //     {
    //         _ignoreNextCurveChange = false;
    //         _lastAnimCurveVersion = curve.ChangeCount;
    //         return;
    //     }
    //
    //     if (curve.ChangeCount == _lastAnimCurveVersion)
    //         return;
    //
    //     _lastAnimCurveVersion = curve.ChangeCount;
    //
    //     if (ApplyCurveLayoutToPages(curve, out var requiresKeyNormalization))
    //     {
    //         _sketchChangeCount++;
    //         _lastSyncedSketchVersion = _sketchChangeCount;
    //     }
    //
    //     if (requiresKeyNormalization)
    //     {
    //         NormalizeKeyValuesToConsecutive(curve);
    //         _ignoreNextCurveChange = true;
    //     }        
    // }
    #endregion
    private void AssignUniqueFilePath()
    {
        if (Parent == null)
            return;

        var composition = Parent.Parent;
        if (composition == null)
            return;

        var pathInput = Parent.Inputs.FirstOrDefault(i => i.Id == _pathPathInputId);
        if (pathInput == null)
            return;

        if (!pathInput.Input.IsDefault)
            return;

        if (pathInput is not InputSlot<string> stringInput)
            return;

        var symbolPackageName = Parent.Parent?.Symbol.SymbolPackage.Name;
        if (string.IsNullOrEmpty(symbolPackageName))
            return;

        var path = $"{symbolPackageName}:sketches/{Parent.SymbolChildId.ShortenGuid()}.json";

        stringInput.SetTypedInputValue(path);
    }

    private void ClearSelection()
    {
        if (_paging.ActivePage == null || CurrentPointList == null)
            return;

        for (var index = 0; index < CurrentPointList.TypedElements.Length; index++)
        {
            CurrentPointList.TypedElements[index].F2 = 0;
        }

        _sketchChangeCount++;
    }

    private void EraseSelection()
    {
        if (_paging.ActivePage == null || CurrentPointList == null)
            return;

        for (var index = 0; index < CurrentPointList.TypedElements.Length; index++)
        {
            var selection = CurrentPointList.TypedElements[index].F2;
            if (selection > 0.9f)
            {
                CurrentPointList.TypedElements[index].Scale = Vector3.One * float.NaN;
            }
        }

        _sketchChangeCount++;
    }

    private void ColorizePage(Vector4 fillColor)
    {
        if (_paging.ActivePage == null || CurrentPointList == null)
            return;

        //Log.Debug("Colorize page " + fillColor, this);

        for (var index = 0; index < CurrentPointList.TypedElements.Length; index++)
        {
            CurrentPointList.TypedElements[index].Color = fillColor;
            _sketchChangeCount++;
        }
    }

    private bool DoSketch(EvaluationContext context, out Vector3 posInWorld, out float visibleBrushSize)
    {
        visibleBrushSize = _brushSize;
        if (_drawMode == DrawModes.Erase)
            visibleBrushSize *= 4;

        posInWorld = CalcPosInWorld(context, MousePos.GetValue(context));

        if (_drawMode == DrawModes.View || !IsOpSelected)
        {
            _isMouseDown = false;
            _currentStrokeLength = 0;
            return false;
        }

        var isMouseDown = IsMouseButtonDown.GetValue(context);
        var justReleased = !isMouseDown && _isMouseDown;
        var justPressed = isMouseDown && !_isMouseDown;
        _isMouseDown = isMouseDown;

        if (justReleased)
        {
            if (_drawMode != DrawModes.Draw || !_paging.HasActivePage)
                return false;

            // Add to points for single click to make it visible as a dot
            var wasClick = _currentStrokeLength == 1;
            if (wasClick)
            {
                if (!GetPreviousStrokePoint(out var clickPoint))
                {
                    return false;
                }

                clickPoint.Position += Vector3.UnitY * 0.02f * 2 * visibleBrushSize;
                AppendPoint(clickPoint);
            }

            AppendPoint(Point.Separator());
            _currentStrokeLength = 0;
            return true;
        }

        if (!_isMouseDown)
            return false;

        if (_currentStrokeLength > 0 && GetPreviousStrokePoint(out var lastPoint))
        {
            var distance = Vector3.Distance(lastPoint.Position, posInWorld);
            var minDistanceForBrushSize = 0.01f;

            var updateLastPoint = distance < visibleBrushSize * minDistanceForBrushSize;
            if (updateLastPoint)
            {
                // Sadly, adding intermedia points causes too many artifacts
                // lastPoint.Position = posInWorld;
                // AppendPoint(lastPoint, advanceIndex: false);
                return false;
            }
        }

        switch (_drawMode)
        {
            case DrawModes.Draw:
                if (_enableKeyframeSync)
                {
                    _keyframeSync.EnsurePageAndKeyAtTime(context.LocalTime, _paging); // curve is the overrideIndex curve
                }
                else
                {
                    if (!_paging.HasActivePage)
                        _paging.InsertNewPage();
                }

                if (justPressed && KeyHandler.PressedKeys[(int)Key.ShiftKey] && _paging.ActivePage!.WriteIndex > 1)
                {
                    // Discard last separator point
                    _paging.ActivePage.WriteIndex--;
                    _currentStrokeLength = 1;
                }

                var color = StrokeColor.GetValue(context);

                AppendPoint(new Point
                                {
                                    Position = posInWorld,
                                    Color = color,
                                    Scale = Vector3.One * (visibleBrushSize / 2 + 0.002f),
                                    F2 = 0, // Not selected by default
                                });
                AppendPoint(Point.Separator(), advanceIndex: false);
                _currentStrokeLength++;
                return true;

            case DrawModes.Erase:
            case DrawModes.Select:
            {
                if (_paging.ActivePage == null || CurrentPointList == null)
                    return false;

                var wasModified = false;
                for (var index = 0; index < CurrentPointList.NumElements; index++)
                {
                    var distanceToPoint = Vector3.Distance(posInWorld, CurrentPointList.TypedElements[index].Position);
                    if (!(distanceToPoint < visibleBrushSize * 0.02f))
                        continue;

                    if (_drawMode == DrawModes.Erase)
                    {
                        CurrentPointList.TypedElements[index].Scale = Vector3.One * float.NaN;
                    }
                    else if (_drawMode == DrawModes.Select)
                    {
                        CurrentPointList.TypedElements[index].F2 = 1;
                    }

                    wasModified = true;
                }

                return wasModified;
            }

            // {
            //     if (_paging.ActivePage == null || CurrentPointList == null)
            //         return false;
            //
            //     var wasModified = false;
            //     for (var index = 0; index < CurrentPointList.NumElements; index++)
            //     {
            //         var distanceToPoint = Vector3.Distance(posInWorld, CurrentPointList.TypedElements[index].Position);
            //         if (!(distanceToPoint < visibleBrushSize * 0.02f))
            //             continue;
            //
            //         CurrentPointList.TypedElements[index].Scale = Vector3.One* float.NaN;
            //         //CurrentPointList.TypedElements[index].F2 = 0.8f;
            //         wasModified = true;
            //     }
            //
            //     return wasModified;
            // }
        }

        return false;
    }

    private static Vector3 CalcPosInWorld(EvaluationContext context, Vector2 mousePos)
    {
        const float offsetFromCamPlane = 0.99f;
        var posInClipSpace = new System.Numerics.Vector4((mousePos.X - 0.5f) * 2, (-mousePos.Y + 0.5f) * 2, offsetFromCamPlane, 1);
        Matrix4x4.Invert(context.CameraToClipSpace, out var clipSpaceToCamera);
        Matrix4x4.Invert(context.WorldToCamera, out var cameraToWorld);
        //Matrix4x4.Invert(context.ObjectToWorld, out var worldToObject);

        var clipSpaceToWorld = Matrix4x4.Multiply(clipSpaceToCamera, cameraToWorld);
        var m = Matrix4x4.Multiply(cameraToWorld, clipSpaceToCamera);
        Matrix4x4.Invert(m, out m);

        var p = Vector4.Transform(posInClipSpace, clipSpaceToWorld);
        return new System.Numerics.Vector3(p.X, p.Y, p.Z) / p.W;
    }

    private void AppendPoint(Point p, bool advanceIndex = true)
    {
        if (_paging.ActivePage == null || CurrentPointList == null)
        {
            Log.Warning("Tried writing to undefined sketch page", this);
            return;
        }

        if (_paging.ActivePage.WriteIndex >= CurrentPointList.NumElements - 1)
        {
            //Log.Debug($"Increasing paint buffer length of {CurrentPointList.NumElements} by {BufferIncreaseStep}...", this);
            CurrentPointList.SetLength(CurrentPointList.NumElements + BufferIncreaseStep);
        }

        CurrentPointList.TypedElements[_paging.ActivePage.WriteIndex] = p;

        if (advanceIndex)
            _paging.ActivePage.WriteIndex++;
    }

    private bool GetPreviousStrokePoint(out Point point)
    {
        if (_paging.ActivePage == null || _currentStrokeLength == 0 || _paging.ActivePage.WriteIndex == 0 || CurrentPointList == null)
        {
            Log.Warning("Can't get previous stroke point", this);
            point = new Point();
            return false;
        }

        point = CurrentPointList.TypedElements[_paging.ActivePage.WriteIndex - 1];
        return true;
    }

    private double _lastModificationTime;
    private StructuredList<Point>? CurrentPointList => _paging.ActivePage?.PointsList;

    private float _brushSize;

    //private bool _needsSave;
    private DrawModes _drawMode = DrawModes.Draw;
    private bool _isMouseDown;

    private int _currentStrokeLength;

    private double _lastUpdateContextTime = -1;

    private bool IsOpSelected => MouseInput.SelectedChildId == Parent?.SymbolChildId;

    internal sealed class Page
    {
        public int WriteIndex;
        public double Time;

        [JsonConverter(typeof(StructuredListConverter))]
        public StructuredList<Point> PointsList = new();
    }

    /// <summary>
    /// Controls switching between different sketch pages
    /// </summary>
    private sealed class Paging
    {
        /// <summary>
        /// Derives active page index from local time or parameter override 
        /// </summary>
        public void UpdatePageIndex(double contextLocalTime, int overridePageIndex)
        {
            _lastContextTime = contextLocalTime;

            if (overridePageIndex >= 0)
            {
                if (overridePageIndex >= Pages.Count)
                {
                    ActivePage = null;
                    return;
                }

                ActivePageIndex = overridePageIndex;
                ActivePage = Pages[overridePageIndex];
                return;
            }

            for (var pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
            {
                var page = Pages[pageIndex];
                if (!(Math.Abs(page.Time - contextLocalTime) < 0.05))
                    continue;

                ActivePageIndex = pageIndex;
                ActivePage = Pages[pageIndex];
                return;
            }

            ActivePageIndex = NoPageIndex;
            ActivePage = null;
        }

        public void InsertNewPage()
        {
            Pages.Add(new Page
                          {
                              Time = _lastContextTime,
                              PointsList = new StructuredList<Point>(BufferIncreaseStep),
                          });
            Pages = Pages.OrderBy(p => p.Time).ToList();
            UpdatePageIndex(_lastContextTime, NoPageIndex); // This is probably bad
        }

        public void LoadPages(string filepath)
        {
            Pages = [];
            try
            {
                try
                {
                    Pages = JsonUtils.TryLoadingJson<List<Page>>(filepath) ?? [];
                }
                catch (Exception e)
                {
                    Log.Debug("Failed reading sketch pages from json: " + e.Message, this);
                }

                foreach (var page in Pages)
                {
                    if (page.PointsList.NumElements == 0)
                    {
                        page.PointsList = new StructuredList<Point>(BufferIncreaseStep);
                        continue;
                    }

                    if (page.PointsList.NumElements > page.WriteIndex)
                        continue;

                    //Log.Warning($"Adjusting writing index {page.WriteIndex} -> {page.PointsList.NumElements}", this);
                    page.WriteIndex = page.PointsList.NumElements + 1;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"Failed to load pages in {filepath}: {e.Message}", this);
            }
        }

        public bool HasActivePage => ActivePage != null;

        public Page? ActivePage;

        public bool HasCutPage => _cutPage != null;

        public void Cut(int overridePageIndex)
        {
            if (ActivePage == null)
                return;

            _cutPage = ActivePage;
            var activeIndex = Pages.IndexOf(ActivePage);

            Pages.Remove(ActivePage);
            if (overridePageIndex >= 0)
            {
                if (activeIndex != overridePageIndex)
                {
                    Log.Warning($"Expected active page index to be {overridePageIndex} not {activeIndex}", this);
                }

                Pages.Insert(activeIndex, new Page
                                              {
                                                  Time = _lastContextTime,
                                                  PointsList = new StructuredList<Point>(BufferIncreaseStep),
                                              });
            }

            //if (overridePageIndex < 0)
            //{
            //
            //}
            // else
            // {
            //     var index = Pages.IndexOf(ActivePage);
            //     if (index != -1)
            //     {
            //         Pages[index] = null;
            //     }
            // }
            UpdatePageIndex(_lastContextTime, overridePageIndex);
        }

        public void Paste(double time, int overridePageIndex)
        {
            if (_cutPage == null || ActivePage == null)
                return;

            if (HasActivePage)
                Pages.Remove(ActivePage);

            _cutPage.Time = time;
            Pages.Add(_cutPage);
            UpdatePageIndex(_lastContextTime, overridePageIndex);
        }

        public int ActivePageIndex { get; private set; } = NoPageIndex;

        public List<Page> Pages = [];
        private Page? _cutPage;
        private double _lastContextTime;

        private const int NoPageIndex = -1;
    }

    private readonly Paging _paging = new();
    private const int BufferIncreaseStep = 100; // low to reduce page file overhead

    private int _lastAnimCurveVersion = -1;
    private int _sketchChangeCount;
    private int _lastSyncedSketchVersion;
    private int _lastSavedSketchVersion;
    private bool _ignoreNextCurveChange;

    private readonly int[] _numberKeys =
        { (int)Key.D1, (int)Key.D2, (int)Key.D3, (int)Key.D4, (int)Key.D5, (int)Key.D6, (int)Key.D7, (int)Key.D8, (int)Key.D9 };

    public enum DrawModes
    {
        View,
        Draw,
        Erase,
        Select,
    }

    public enum ColorModes
    {
        Stroke,
        Page,
    }

    private readonly Guid _pathPathInputId = new("2ded8235-157d-486b-a997-87d09d18f998");
    private readonly Guid _overrideKeyframeIndexInputId = new("37093302-053a-47b2-ace6-b9d310d3f4b7");

    [Input(Guid = "C427F009-7E04-4168-82E6-5EBE2640204D")]
    public readonly InputSlot<Vector2> MousePos = new();

    [Input(Guid = "520A2023-7450-4314-9CAC-850D6D692461")]
    public readonly InputSlot<bool> IsMouseButtonDown = new();

    [Input(Guid = "1057313C-006A-4F12-8828-07447337898B")]
    public readonly InputSlot<float> StrokeSize = new();

    [Input(Guid = "AE7FB135-C216-4F34-B73F-5115417E916B")]
    public readonly InputSlot<Vector4> StrokeColor = new();

    [Input(Guid = "37558056-88D8-4D2E-89E2-9F3460565BC8", MappedType = typeof(ColorModes))]
    public readonly InputSlot<int> ColorMode = new();

    [Input(Guid = "51641425-A2C6-4480-AC8F-2E6D2CBC300A")]
    public readonly InputSlot<string> FilePath = new();

    [Input(Guid = "0FA40E27-C7CA-4BB9-88C6-CED917DFEC12")]
    public readonly InputSlot<int> OverridePageIndex = new();

    [Input(Guid = "D156BD8F-F2A7-47F2-BF14-99BE4AAD32D7")]
    public readonly InputSlot<bool> EnableKeyframeSync = new();

    private sealed class KeyframeSync
    {
        private readonly _SketchImpl _owner;
        private int _lastSeenCurveRevision = -1;
        private int _pendingCurveRevision = -1;
        private int _appliedCurveRevision = -1;
        private int _stableFrames;
        private int _lastSeenSketchRevision = -1;
        private bool _ignoreNextCurveChange;
        private const int DebounceStableFrames = 2; // or 3

        public KeyframeSync(_SketchImpl owner)
        {
            _owner = owner;
        }

        private bool TryGetOverrideCurve( [NotNullWhen(true)] out Curve? curve)
        {
            curve = null;
            var composition = _owner.Parent?.Parent;
            if (composition == null)
                return false;

            if (!composition.Symbol.Animator.TryGetCurvesForChildInput(_owner.Parent!.SymbolChildId, _owner._overrideKeyframeIndexInputId, out var curves))
                return false;

            if (curves.Length != 1)
                return false;

            curve = curves[0];
            return true;
        }
        
        public void UpdateIfEnabled(bool enabled, EvaluationContext context)
        {
            if (!enabled)
                return;

            if (!TryGetOverrideCurve(out var curve))
                return;

            // 1) Sketch/pages changed -> push to curve by time
            // You can make this stricter (only react to insert/delete), but this matches your stated intent.
            if (_owner._sketchChangeCount != _lastSeenSketchRevision)
            {
                
                SyncCurveToPagesByTimeFromSketch(curve, _owner._paging.Pages);

                _lastSeenSketchRevision = _owner._sketchChangeCount;
                _ignoreNextCurveChange = true;
                _lastSeenCurveRevision = curve.ChangeCount;
                return;
            }

            
            // 2) Curve changed by user -> debounce heavy sync
            if (curve.ChangeCount != _pendingCurveRevision)
            {
                _pendingCurveRevision = curve.ChangeCount;
                _stableFrames = 0;

                // cheap live update for responsiveness
                ApplyKeyTimesToPagesLive(_owner._paging, curve);
                return;
            }

            _stableFrames++;
            if (_stableFrames < DebounceStableFrames)
            {
                ApplyKeyTimesToPagesLive(_owner._paging, curve);
                return;
            }

            // curve stable for a couple frames -> do heavy reconcile once
            if (_ignoreNextCurveChange)
            {
                _ignoreNextCurveChange = false;
                _appliedCurveRevision = curve.ChangeCount;
                return;
            }

            if (_appliedCurveRevision == curve.ChangeCount)
                return;

            _appliedCurveRevision = curve.ChangeCount;

            SyncPagesFromCurveByIndexEdits(_owner._paging, curve);

            var keyframes = curve.GetVDefinitions();
            if (RequiresNormalization(keyframes))
            {
                NormalizeKeyValuesByTime(curve);
                _ignoreNextCurveChange = true;
            }

            // mark sketch modified
            _owner._sketchChangeCount++;
            _lastSeenSketchRevision = _owner._sketchChangeCount;
        }

        private static void ApplyKeyTimesToPagesLive(Paging paging, Curve curve)
        {
            var keys = curve.GetVDefinitions();

            // Keep it cheap: only touch common prefix
            var n = Math.Min(keys.Count, paging.Pages.Count);
            for (var i = 0; i < n; i++)
                paging.Pages[i].Time = keys[i].U;

            // No insert/remove pages, no normalization, no curve writes.
        }
        
        // ----------------------------
        // Case A: sketch updated -> match keys to pages by time, add/remove keys, normalize
        // ----------------------------
        private static void SyncCurveToPagesByTimeFromSketch(Curve curve,
                                                             IReadOnlyList<_SketchImpl.Page> pages,
                                                             double matchEps = 1e-3)
        {
            var keys = curve.GetVDefinitions(); // ordered
            var keyUsed = new bool[keys.Count];

            // pages sorted by time are truth
            var pagesByTime = pages.OrderBy(p => p.Time).ToList();

            // build explicit mapping: move this key time -> that page time
            var moves = new List<(double OldKeyTime, double NewKeyTime)>(pagesByTime.Count);
            var adds = new List<double>(pagesByTime.Count);

            var k = 0;
            foreach (var page in pagesByTime)
            {
                while (k < keys.Count && keys[k].U < page.Time - matchEps)
                    k++;

                var best = -1;
                var bestDist = double.MaxValue;

                for (var j = k; j < keys.Count; j++)
                {
                    var dt = keys[j].U - page.Time;
                    if (dt > matchEps)
                        break;

                    if (keyUsed[j])
                        continue;

                    var dist = Math.Abs(dt);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = j;
                    }
                }

                if (best >= 0)
                {
                    keyUsed[best] = true;
                    moves.Add((keys[best].U, page.Time)); // preserve this exact key (selection etc.)
                }
                else
                {
                    adds.Add(page.Time); // page without matching key => new key
                }
            }

            // remove unused old keys
            for (var i = 0; i < keys.Count; i++)
            {
                if (!keyUsed[i])
                    curve.RemoveKeyframeAt(keys[i].U);
            }

            // move matched keys (do after removals if MoveKey expects old time to still exist;
            // if it fails when old time removed, swap order: move first, then remove)
            foreach (var (oldT, newT) in moves)
                curve.MoveKey(oldT, newT);

            // add new keys
            foreach (var t in adds)
                curve.AddOrUpdateV(t, new VDefinition { Value = 0 , InType = VDefinition.Interpolation.Constant, OutType = VDefinition.Interpolation.Constant});

            NormalizeKeyValuesByTime(curve);
        }

        /// <summary>
        /// We attempt to move keys, to keep selection etc.
        /// </summary>
        private static void RebuildCurveKeys(Curve curve, IReadOnlyList<double> oldTimes, IReadOnlyList<double> newTimes)
        {
            if (oldTimes.Count == newTimes.Count)
            {
                for (var index = 0; index < oldTimes.Count; index++)
                {
                    curve.MoveKey(oldTimes[index], newTimes[index]);
                }
            }
            else
            {
                foreach (var t in oldTimes)
                {
                    curve.RemoveKeyframeAt(t);
                }

                for (var i = 0; i < newTimes.Count; i++)
                {
                    curve.AddOrUpdateV(newTimes[i], new VDefinition { Value = i, InType = VDefinition.Interpolation.Constant, OutType = VDefinition.Interpolation.Constant});
                }
            }
        }

        /// <summary>
        /// Case B: keyframes changed -> treat Value as old index, handle gaps + duplicates, normalize 
        /// </summary>
        private static void SyncPagesFromCurveByIndexEdits(_SketchImpl.Paging paging, Curve curve)
        {
            var keys = curve.GetVDefinitions();
            var oldPages = paging.Pages; // current order == previous indices
            var usedOldIndex = new bool[oldPages.Count];
            var newPages = new List<_SketchImpl.Page>(keys.Count);

            var occurrence = new Dictionary<int, int>();

            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];

                var idx = (int)Math.Round(key.Value);
                occurrence.TryGetValue(idx, out var occ);
                occurrence[idx] = occ + 1;

                if (occ == 0)
                {
                    // First occurrence: keep page at that old index (if exists), otherwise blank
                    if (idx >= 0 && idx < oldPages.Count && !usedOldIndex[idx])
                    {
                        var page = oldPages[idx];
                        usedOldIndex[idx] = true;
                        page.Time = key.U;
                        newPages.Add(page);
                    }
                    else
                    {
                        newPages.Add(CreateBlankPage(key.U));
                    }
                }
                else
                {
                    // Duplicate: insert blank page at this key time
                    newPages.Add(CreateBlankPage(key.U));
                }
            }

            // Pages not referenced by any key index are removed (gap removal anywhere, not just tail)
            paging.Pages.Clear();
            paging.Pages.AddRange(newPages);
        }

        private static _SketchImpl.Page CreateBlankPage(double time)
        {
            return new _SketchImpl.Page
                       {
                           Time = time,
                           PointsList = new StructuredList<Point>(_SketchImpl.BufferIncreaseStep),
                           WriteIndex = 0,
                       };
        }

        // ----------------------------
        // Normalization using your ordered key access
        // ----------------------------
        private static bool RequiresNormalization(IList<VDefinition> keyframes)
        {
            for (var i = 0; i < keyframes.Count; i++)
            {
                var expected = i;
                var actualRounded = (int)Math.Round(keyframes[i].Value);
                if (actualRounded != expected)
                    return true;
            }

            return false;
        }

        private static void NormalizeKeyValuesByTime(Curve curve)
        {
            var keyframes = curve.GetVDefinitions();
            for (var i = 0; i < keyframes.Count; i++)
            {
                var t = keyframes[i].U;
                curve.AddOrUpdateV(t, new VDefinition { Value = i, InType = VDefinition.Interpolation.Constant, OutType = VDefinition.Interpolation.Constant });
            }
        }
        
        
        public void EnsurePageAndKeyAtTime(double t, Paging paging, double eps = 1e-3)
        {
            if (!TryGetOverrideCurve(out var curve))
                return;
            
            var keys = curve.GetVDefinitions(); // ordered

            // already have a key at time?
            if (curve.HasVAt(t))
                return;
            
            // insertion index = number of keys strictly before t
            var insertIndex = 0;
            while (insertIndex < keys.Count && keys[insertIndex].U < t)
                insertIndex++;

            // insert blank page at that index
            paging.Pages.Insert(insertIndex, new Page
                                                 {
                                                     Time = t,
                                                     PointsList = new StructuredList<Point>(BufferIncreaseStep),
                                                     WriteIndex = 0,
                                                 });

            // insert key at time t (value will be normalized)
            curve.AddOrUpdateV(t, new VDefinition { Value = insertIndex, InType = VDefinition.Interpolation.Constant, OutType = VDefinition.Interpolation.Constant});

            // normalize key values to 0..N-1 in key order
            //NormalizeKeyValuesByTime(curve);

            // keep pages in the same order as keys (we already inserted by key order)
            // then snap page times to key times (optional but nice)
            var keysAfter = curve.GetVDefinitions();
            for (var i = 0; i < keysAfter.Count && i < paging.Pages.Count; i++)
                paging.Pages[i].Time = keysAfter[i].U;
        }

    }

    private readonly KeyframeSync _keyframeSync;
}