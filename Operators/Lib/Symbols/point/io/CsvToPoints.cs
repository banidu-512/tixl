#nullable enable
using System.Globalization;
using T3.Core.Utils;

namespace Lib.point.io;

/// <summary>
/// Loads a CSV file and routes its columns into the attributes of a point buffer.
/// </summary>
[Guid("3e2b155e-52cd-4a58-9b7c-602d7a36e19c")]
internal sealed class CsvToPoints : Instance<CsvToPoints>, IStatusProvider, ICustomDropdownHolder
{
    public enum RotationModes
    {
        EulerXYZ,
        Quaternion,
    }

    private struct ColumnMap : IEquatable<ColumnMap>
    {
        public int PositionX;
        public int PositionY;
        public int PositionZ;
        public int RotationW;
        public int RotationX;
        public int RotationY;
        public int RotationZ;
        public int ColorR;
        public int ColorG;
        public int ColorB;
        public int ColorA;
        public int ScaleX;
        public int ScaleY;
        public int ScaleZ;
        public int F1;
        public int F2;

        public bool Equals(ColumnMap other)
        {
            return PositionX == other.PositionX
                   && PositionY == other.PositionY
                   && PositionZ == other.PositionZ
                   && RotationW == other.RotationW
                   && RotationX == other.RotationX
                   && RotationY == other.RotationY
                   && RotationZ == other.RotationZ
                   && ColorR == other.ColorR
                   && ColorG == other.ColorG
                   && ColorB == other.ColorB
                   && ColorA == other.ColorA
                   && ScaleX == other.ScaleX
                   && ScaleY == other.ScaleY
                   && ScaleZ == other.ScaleZ
                   && F1 == other.F1
                   && F2 == other.F2;
        }

        public override bool Equals(object? obj)
        {
            return obj is ColumnMap other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(PositionX);
            hash.Add(PositionY);
            hash.Add(PositionZ);
            hash.Add(RotationW);
            hash.Add(RotationX);
            hash.Add(RotationY);
            hash.Add(RotationZ);
            hash.Add(ColorR);
            hash.Add(ColorG);
            hash.Add(ColorB);
            hash.Add(ColorA);
            hash.Add(ScaleX);
            hash.Add(ScaleY);
            hash.Add(ScaleZ);
            hash.Add(F1);
            hash.Add(F2);
            return hash.ToHashCode();
        }
    }

    [Output(Guid = "013eb3d8-8e97-44fc-83e4-c777231d8434")]
    public readonly Slot<BufferWithViews?> Points = new();

    [Input(Guid = "537605b3-5d85-4989-bf95-3324107b34c7")]
    public readonly InputSlot<string> FilePath = new();

    [Input(Guid = "d8311eeb-ab1e-4d51-bb6c-3a416868a4a5")]
    public readonly InputSlot<bool> Reload = new();

    [Input(Guid = "20802e48-71d9-4636-b47a-dc0d3271306a", MappedType = typeof(RotationModes))]
    public readonly InputSlot<int> RotationMode = new();

    [Input(Guid = "dce9bb44-a12e-4c28-876d-d6fd7c19af16")]
    public readonly InputSlot<string> RotationW = new();

    [Input(Guid = "e03929c8-b50f-4c46-b283-74b76d1fcc58")]
    public readonly InputSlot<string> RotationX = new();

    [Input(Guid = "a1037cf2-10db-432e-a65d-526e2cd58583")]
    public readonly InputSlot<string> RotationY = new();

    [Input(Guid = "074a03c5-08a8-4edc-9906-edef98dbb754")]
    public readonly InputSlot<string> RotationZ = new();

    [Input(Guid = "1b668fd3-5ad8-4e50-8ad3-359d3de9b404")]
    public readonly InputSlot<string> PositionX = new();

    [Input(Guid = "1074083c-0ede-4821-9a64-381fc19502dc")]
    public readonly InputSlot<string> PositionY = new();

    [Input(Guid = "ba625921-0efd-49f9-b7f5-b335bb39c6c3")]
    public readonly InputSlot<string> PositionZ = new();

    [Input(Guid = "7854f719-d7a4-42c5-b52d-22ae1f0e76b1")]
    public readonly InputSlot<string> ColorR = new();

    [Input(Guid = "723a2460-2db4-485d-9763-42f00c987350")]
    public readonly InputSlot<string> ColorG = new();

    [Input(Guid = "4cf2dead-6843-4ec6-a8b7-a639b4bb63f4")]
    public readonly InputSlot<string> ColorB = new();

    [Input(Guid = "1ad9c3d6-5497-4be4-8f3d-ed935ca7e1ae")]
    public readonly InputSlot<string> ColorA = new();

    [Input(Guid = "c2d008e4-5553-465c-a9fc-b8c7d41f2296")]
    public readonly InputSlot<bool> ColorIs0To255 = new();

    [Input(Guid = "bd3a1311-caee-4283-83a4-59b93a971d38")]
    public readonly InputSlot<string> ScaleX = new();

    [Input(Guid = "cc323942-e0e7-4a66-84d3-066fa23c3ed7")]
    public readonly InputSlot<string> ScaleY = new();

    [Input(Guid = "73b148ac-4669-4738-b18d-4790adf174ba")]
    public readonly InputSlot<string> ScaleZ = new();

    [Input(Guid = "eaefda89-f7ad-4456-be37-ed9d4e6b0245")]
    public readonly InputSlot<string> F1 = new();

    [Input(Guid = "2d59cb93-d544-42c9-ade4-2f871110ca0d")]
    public readonly InputSlot<string> F2 = new();

    public CsvToPoints()
    {
        Points.UpdateAction += Update;
    }

    IStatusProvider.StatusLevel IStatusProvider.GetStatusLevel()
    {
        return _statusLevel;
    }

    string? IStatusProvider.GetStatusMessage()
    {
        return _statusMessage;
    }

    string? ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        return GetMappingSlot(inputId)?.Value ?? string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        var path = FilePath.Value ?? string.Empty;
        if (!string.Equals(path, _headerSourcePath, StringComparison.Ordinal))
        {
            LoadColumnNames(path);
        }

        return _columnOptions;
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        var slot = GetMappingSlot(inputId);
        if (slot == null || selected == null)
        {
            return;
        }

        slot.SetTypedInputValue(selected == NoColumnOption ? string.Empty : selected);
    }

    private void Update(EvaluationContext context)
    {
        var reloadRequested = MathUtils.WasTriggered(Reload.GetValue(context), ref _reloadTriggered);
        if (reloadRequested)
        {
            Reload.SetTypedInputValue(false);
        }

        var path = FilePath.GetValue(context) ?? string.Empty;
        var rotationMode = (RotationModes)RotationMode.GetValue(context);
        var colorIs0To255 = ColorIs0To255.GetValue(context);

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!string.IsNullOrEmpty(_headerSourcePath))
            {
                LoadColumnNames(string.Empty);
            }

            _loadedPath = string.Empty;
            _appliedMap = UnmappedColumns;
            _appliedRotationMode = rotationMode;
            _appliedColorIs0To255 = colorIs0To255;
            UpdatePointBuffer(null);
            SetStatus("No CSV file selected.", IStatusProvider.StatusLevel.Notice);
            Points.Value = _pointBuffer;
            return;
        }

        var fileChanged = reloadRequested || !string.Equals(path, _loadedPath, StringComparison.Ordinal);

        if (fileChanged)
        {
            if (LoadFile(path))
            {
                _appliedMap = ResolveColumns(context);
                _appliedRotationMode = rotationMode;
                _appliedColorIs0To255 = colorIs0To255;
                RebuildPoints(_appliedMap, rotationMode, colorIs0To255);
            }
            else
            {
                _appliedMap = UnmappedColumns;
                _appliedRotationMode = rotationMode;
                _appliedColorIs0To255 = colorIs0To255;
            }
        }
        else
        {
            var map = ResolveColumns(context);
            if (!map.Equals(_appliedMap)
                || rotationMode != _appliedRotationMode
                || colorIs0To255 != _appliedColorIs0To255)
            {
                _appliedMap = map;
                _appliedRotationMode = rotationMode;
                _appliedColorIs0To255 = colorIs0To255;
                RebuildPoints(map, rotationMode, colorIs0To255);
            }
        }

        Points.Value = _pointBuffer;
    }

    private bool LoadFile(string path)
    {
        _loadedPath = path;
        _headerSourcePath = path;
        _rows = null;
        _headers.Clear();
        _headerIndices.Clear();
        _columnOptions.Clear();
        _columnOptions.Add(NoColumnOption);

        if (!TryGetFilePath(path, out var resolvedPath))
        {
            UpdatePointBuffer(null);
            SetStatus($"File not found: {path}", IStatusProvider.StatusLevel.Warning);
            return false;
        }

        try
        {
            var rows = ParseCsv(File.ReadAllText(resolvedPath));
            if (rows.Count == 0)
            {
                UpdatePointBuffer(null);
                SetStatus("The CSV file is empty.", IStatusProvider.StatusLevel.Warning);
                return false;
            }

            _headers.AddRange(rows[0]);
            rows.RemoveAt(0);

            for (var i = 0; i < _headers.Count; i++)
            {
                var header = _headers[i].Trim().TrimStart('\uFEFF');
                _headers[i] = header;
                if (header.Length > 0)
                {
                    _headerIndices.TryAdd(header, i);
                }
            }

            _rows = rows;
            RefreshColumnOptions();
            return true;
        }
        catch (Exception exception)
        {
            UpdatePointBuffer(null);
            SetStatus($"Failed to read CSV: {exception.Message}", IStatusProvider.StatusLevel.Error);
            return false;
        }
    }

    private void LoadColumnNames(string path)
    {
        _headerSourcePath = path;
        _columnOptions.Clear();
        _columnOptions.Add(NoColumnOption);

        if (string.IsNullOrWhiteSpace(path) || !TryGetFilePath(path, out var resolvedPath))
        {
            return;
        }

        try
        {
            using var reader = new StreamReader(resolvedPath);
            var firstLine = reader.ReadLine();
            if (firstLine == null)
            {
                return;
            }

            var rows = ParseCsv(firstLine);
            if (rows.Count == 0)
            {
                return;
            }

            var headers = rows[0];
            for (var i = 0; i < headers.Length; i++)
            {
                var header = headers[i].Trim().TrimStart('\uFEFF');
                if (header.Length > 0)
                {
                    _columnOptions.Add(header);
                }
            }
        }
        catch
        {
            // The evaluation pass reports unreadable files; the dropdown just stays empty here.
        }
    }

    private void RefreshColumnOptions()
    {
        _columnOptions.Clear();
        _columnOptions.Add(NoColumnOption);
        for (var i = 0; i < _headers.Count; i++)
        {
            var header = _headers[i];
            if (header.Length > 0)
            {
                _columnOptions.Add(header);
            }
        }
    }

    private ColumnMap ResolveColumns(EvaluationContext context)
    {
        return new ColumnMap
                   {
                       PositionX = ResolveIndex(PositionX.GetValue(context)),
                       PositionY = ResolveIndex(PositionY.GetValue(context)),
                       PositionZ = ResolveIndex(PositionZ.GetValue(context)),
                       RotationW = ResolveIndex(RotationW.GetValue(context)),
                       RotationX = ResolveIndex(RotationX.GetValue(context)),
                       RotationY = ResolveIndex(RotationY.GetValue(context)),
                       RotationZ = ResolveIndex(RotationZ.GetValue(context)),
                       ColorR = ResolveIndex(ColorR.GetValue(context)),
                       ColorG = ResolveIndex(ColorG.GetValue(context)),
                       ColorB = ResolveIndex(ColorB.GetValue(context)),
                       ColorA = ResolveIndex(ColorA.GetValue(context)),
                       ScaleX = ResolveIndex(ScaleX.GetValue(context)),
                       ScaleY = ResolveIndex(ScaleY.GetValue(context)),
                       ScaleZ = ResolveIndex(ScaleZ.GetValue(context)),
                       F1 = ResolveIndex(F1.GetValue(context)),
                       F2 = ResolveIndex(F2.GetValue(context)),
                   };
    }

    private int ResolveIndex(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return -1;
        }

        return _headerIndices.TryGetValue(columnName, out var index) ? index : -1;
    }

    private void RebuildPoints(in ColumnMap map, RotationModes rotationMode, bool colorIs0To255)
    {
        if (_rows == null || _rows.Count == 0)
        {
            UpdatePointBuffer(null);
            SetStatus("The CSV has no data rows.", IStatusProvider.StatusLevel.Warning);
            return;
        }

        var points = new List<Point>(_rows.Count);
        foreach (var row in _rows)
        {
            points.Add(new Point
                           {
                               Position = new Vector3(GetFloat(row, map.PositionX, 0),
                                                      GetFloat(row, map.PositionY, 0),
                                                      GetFloat(row, map.PositionZ, 0)),
                               F1 = GetFloat(row, map.F1, 1),
                               F2 = GetFloat(row, map.F2, 1),
                               Color = new Vector4(GetColor(row, map.ColorR, colorIs0To255),
                                                   GetColor(row, map.ColorG, colorIs0To255),
                                                   GetColor(row, map.ColorB, colorIs0To255),
                                                   GetColor(row, map.ColorA, colorIs0To255)),
                               Scale = new Vector3(GetFloat(row, map.ScaleX, 1),
                                                   GetFloat(row, map.ScaleY, 1),
                                                   GetFloat(row, map.ScaleZ, 1)),
                               Orientation = BuildOrientation(GetFloat(row, map.RotationW, 1),
                                                              GetFloat(row, map.RotationX, 0),
                                                              GetFloat(row, map.RotationY, 0),
                                                              GetFloat(row, map.RotationZ, 0),
                                                              rotationMode),
                           });
        }

        UpdatePointBuffer(points);
        SetStatus($"Loaded {points.Count} points · {CountMapped(map)} attributes mapped", IStatusProvider.StatusLevel.Success);
    }

    private static Quaternion BuildOrientation(float w, float x, float y, float z, RotationModes rotationMode)
    {
        if (rotationMode == RotationModes.Quaternion)
        {
            var quaternion = new Quaternion(x, y, z, w);
            return quaternion.LengthSquared() > 0.000001f ? Quaternion.Normalize(quaternion) : Quaternion.Identity;
        }

        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x * (MathF.PI / 180f));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, y * (MathF.PI / 180f));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z * (MathF.PI / 180f));
        // Euler order XYZ rotates around Z first, then Y, then X.
        return Quaternion.Concatenate(Quaternion.Concatenate(qz, qy), qx);
    }

    private static float GetColor(string[] row, int index, bool colorIs0To255)
    {
        if (index < 0 || index >= row.Length || !TryParseNumber(row[index], out var value))
        {
            return 1;
        }

        return colorIs0To255 ? value / 255f : value;
    }

    private static float GetFloat(string[] row, int index, float fallback)
    {
        if (index < 0 || index >= row.Length || !TryParseNumber(row[index], out var value))
        {
            return fallback;
        }

        return value;
    }

    private static bool TryParseNumber(string raw, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.AsSpan().Trim();
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return float.IsFinite(value);
        }

        // Fixture exports append units ("-2.25m", "26.8°", "300W"); retry without the unit suffix only.
        var end = text.Length;
        while (end > 0 && (char.IsLetter(text[end - 1]) || text[end - 1] is '°' or '%'))
        {
            end--;
        }

        return end > 0
               && end < text.Length
               && float.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && float.IsFinite(value);
    }

    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    AddRow(rows, fields, field);
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            AddRow(rows, fields, field);
        }

        return rows;
    }

    private static void AddRow(List<string[]> rows, List<string> fields, StringBuilder field)
    {
        fields.Add(field.ToString());
        field.Clear();

        var isEmpty = true;
        for (var i = 0; i < fields.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(fields[i]))
            {
                isEmpty = false;
                break;
            }
        }

        if (!isEmpty)
        {
            rows.Add(fields.ToArray());
        }

        fields.Clear();
    }

    private static int CountMapped(in ColumnMap map)
    {
        var count = 0;
        if (map.PositionX >= 0) count++;
        if (map.PositionY >= 0) count++;
        if (map.PositionZ >= 0) count++;
        if (map.RotationW >= 0) count++;
        if (map.RotationX >= 0) count++;
        if (map.RotationY >= 0) count++;
        if (map.RotationZ >= 0) count++;
        if (map.ColorR >= 0) count++;
        if (map.ColorG >= 0) count++;
        if (map.ColorB >= 0) count++;
        if (map.ColorA >= 0) count++;
        if (map.ScaleX >= 0) count++;
        if (map.ScaleY >= 0) count++;
        if (map.ScaleZ >= 0) count++;
        if (map.F1 >= 0) count++;
        if (map.F2 >= 0) count++;
        return count;
    }

    private void UpdatePointBuffer(List<Point>? points)
    {
        var count = points?.Count ?? 0;
        if (count == 0)
        {
            _pointBuffer?.Dispose();
            _pointBuffer = null;
            return;
        }

        var currentCount = _pointBuffer?.Buffer != null ? _pointBuffer.Buffer.Description.SizeInBytes / Point.Stride : 0;
        if (count != currentCount)
        {
            _pointBuffer?.Dispose();
            _pointBuffer = null;
        }

        var array = points!.ToArray();
        if (_pointBuffer == null)
        {
            _pointBuffer = new BufferWithViews();
            ResourceManager.SetupStructuredBuffer(array, Point.Stride * count, Point.Stride, ref _pointBuffer.Buffer);
            ResourceManager.CreateStructuredBufferSrv(_pointBuffer.Buffer, ref _pointBuffer.Srv);
            ResourceManager.CreateStructuredBufferUav(_pointBuffer.Buffer, UnorderedAccessViewBufferFlags.None, ref _pointBuffer.Uav);
        }
        else
        {
            ResourceManager.Device.ImmediateContext.UpdateSubresource(array, _pointBuffer.Buffer);
        }
    }

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
    }

    private InputSlot<string>? GetMappingSlot(Guid inputId)
    {
        if (inputId == PositionX.Id) return PositionX;
        if (inputId == PositionY.Id) return PositionY;
        if (inputId == PositionZ.Id) return PositionZ;
        if (inputId == RotationW.Id) return RotationW;
        if (inputId == RotationX.Id) return RotationX;
        if (inputId == RotationY.Id) return RotationY;
        if (inputId == RotationZ.Id) return RotationZ;
        if (inputId == ColorR.Id) return ColorR;
        if (inputId == ColorG.Id) return ColorG;
        if (inputId == ColorB.Id) return ColorB;
        if (inputId == ColorA.Id) return ColorA;
        if (inputId == ScaleX.Id) return ScaleX;
        if (inputId == ScaleY.Id) return ScaleY;
        if (inputId == ScaleZ.Id) return ScaleZ;
        if (inputId == F1.Id) return F1;
        if (inputId == F2.Id) return F2;
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pointBuffer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static readonly ColumnMap UnmappedColumns = new()
                                                            {
                                                                PositionX = -1,
                                                                PositionY = -1,
                                                                PositionZ = -1,
                                                                RotationW = -1,
                                                                RotationX = -1,
                                                                RotationY = -1,
                                                                RotationZ = -1,
                                                                ColorR = -1,
                                                                ColorG = -1,
                                                                ColorB = -1,
                                                                ColorA = -1,
                                                                ScaleX = -1,
                                                                ScaleY = -1,
                                                                ScaleZ = -1,
                                                                F1 = -1,
                                                                F2 = -1,
                                                            };

    private const string NoColumnOption = "(none)";

    private readonly List<string> _columnOptions = new() { NoColumnOption };
    private readonly List<string> _headers = new();
    private readonly Dictionary<string, int> _headerIndices = new(StringComparer.OrdinalIgnoreCase);
    private string _headerSourcePath = string.Empty;
    private string _loadedPath = string.Empty;
    private List<string[]>? _rows;
    private BufferWithViews? _pointBuffer;
    private ColumnMap _appliedMap = UnmappedColumns;
    private RotationModes _appliedRotationMode;
    private bool _appliedColorIs0To255;
    private bool _reloadTriggered;
    private string _statusMessage = "No CSV file selected.";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
}
