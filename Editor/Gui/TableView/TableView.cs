using System.Reflection;
using ImGuiNET;
using T3.Core.DataTypes;
using T3.Editor.Gui.Styling;

namespace T3.Editor.Gui.TableView;

public static class TableList
{
    public static bool Draw(StructuredList list)
    {
        return Draw(list, Vector2.Zero);
    }

    public static Dictionary<Type, string[]> TypeComponents = new()
                                                                  {
                                                                      { typeof(Vector2), new[] { ".x", ".y" } },
                                                                      { typeof(Vector3), new[] { ".x", ".y", ".z" } },
                                                                      { typeof(Vector4), new[] { ".R", ".G", ".B", ".A" } },
                                                                      { typeof(Quaternion), new[] { ".x", ".y", ".z", ".w" } },
                                                                  };

    public static bool Draw(StructuredList list, Vector2 size)
    {
        ImGui.BeginChild("child", size);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
        var valueColumnWidth = 50 * T3Ui.UiScaleFactor;
        // ILDA coordinates are six characters wide with the sign ("-32768"). A 50px cell can't hold
        // that next to a drag handle, so integer columns get their own width.
        var intColumnWidth = 80 * T3Ui.UiScaleFactor;
        var lineNumberWidth = 40 * T3Ui.UiScaleFactor;
        var headerHeight = 30 * T3Ui.UiScaleFactor;
        var listModified = false;
        
        ImGui.PushFont(Fonts.FontSmall);
        {
            // Instance fields only: GetFields() with default flags also returns public static
            // fields, so LaserPoint's "Zero" sentinel showed up as an extra column that rendered
            // "?" - and editing it would have written to the static, not the element.
            FieldInfo[] members = list.Type.GetFields(BindingFlags.Public | BindingFlags.Instance);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"[{list.NumElements}]");
            ImGui.SameLine(lineNumberWidth);

            // List Header 

            for (var fieldIndex = 0; fieldIndex < members.Length; fieldIndex++)
            {
                var fi = members[fieldIndex];
                var cellWidth = fi.FieldType == typeof(int) ? intColumnWidth : valueColumnWidth;
                if (TypeComponents.TryGetValue(fi.FieldType, out var components))
                {
                    bool isFirst = true;
                    
                    foreach (var c in components)
                    {
                        ImGui.PushID(fi.Name + c);
                        ImGui.Selectable((isFirst ? " " + fi.Name : "_") + "\n" + c, 
                                         false, 
                                         ImGuiSelectableFlags.None,
                                         new Vector2(valueColumnWidth, headerHeight));
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(fieldIndex + ": " +fi.Name + c );
                        }
                        ImGui.PopID();    
                        ImGui.SameLine();
                        isFirst = false;
                        
                    }
                }
                else
                {
                    ImGui.Selectable(" " + fi.Name, 
                                     false, ImGuiSelectableFlags.None, 
                                     new Vector2(cellWidth, 
                                                 headerHeight));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(fieldIndex + ": " +fi.Name);
                    }
                }

                ImGui.SameLine();
            }

            ImGui.NewLine();
            
            // Values
            for (var objectIndex = 0; objectIndex < Math.Min(list.NumElements, 9999); objectIndex++)
            {
                var cursorScreenPos = ImGui.GetCursorScreenPos();

                var isLineVisible = ImGui.IsRectVisible(cursorScreenPos,
                                                        cursorScreenPos + new Vector2(1000, ImGui.GetFrameHeight()));

                if (!isLineVisible)
                {
                    ImGui.Dummy(new Vector2(1, ImGui.GetFrameHeight()));
                    continue;
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(objectIndex + ".");
                ImGui.SameLine(lineNumberWidth);

                ImGui.PushID(objectIndex);
                var obj = list[objectIndex];

                var objModified = false;
                for (var fieldIndex = 0; fieldIndex < members.Length; fieldIndex++)
                {
                    FieldInfo fi = members[fieldIndex];
                    var o = fi.GetValue(obj);
                    if (o is float f)
                    {
                        if (DrawFloatManipulation(ref f, objectIndex* 1000+ fieldIndex))
                        {
                            fi.SetValue(obj, f);
                            objModified = true;
                        }
                    }
                    else if (o is int integer)
                    {
                        // Integer struct fields (ILDA laser coordinates, counts) were rendered as
                        // "?" before - unreadable for exactly the lists users inspect most.
                        // Step 0: InputInt's default -/+ buttons eat the whole cell and clip the
                        // number, which is the value you came to read.
                        ImGui.PushID(fi.Name);
                        ImGui.SetNextItemWidth(intColumnWidth);
                        if (ImGui.InputInt("##int", ref integer, 0, 0))
                        {
                            fi.SetValue(obj, integer);
                            objModified = true;
                        }

                        ImGui.SameLine();
                        ImGui.PopID();
                    }
                    else if (o is bool flag)
                    {
                        ImGui.PushID(fi.Name);
                        if (ImGui.Checkbox("##bool", ref flag))
                        {
                            fi.SetValue(obj, flag);
                            objModified = true;
                        }

                        ImGui.SameLine();
                        ImGui.PopID();
                    }
                    else if (fi.FieldType == typeof(string))
                    {
                        if (!(o is string s))
                            s = string.Empty;

                        ImGui.PushID(fi.Name);
                        ImGui.SetNextItemWidth(valueColumnWidth);
                        if (ImGui.InputText("##sdf", ref s, 256))
                        {
                            fi.SetValue(obj, s);
                            objModified = true;
                        }

                        ImGui.SameLine();
                        ImGui.PopID();
                    }
                    else if (o is Vector2 vector2)
                    {
                        if (DrawFloatManipulation(ref vector2.X, fieldIndex, 0)
                            | DrawFloatManipulation(ref vector2.Y, fieldIndex, 1))
                        {
                            fi.SetValue(obj, vector2);
                            objModified = true;
                        }
                    }
                    else if (o is Vector3 vector3)
                    {
                        if (DrawFloatManipulation(ref vector3.X, fieldIndex, 0)
                            | DrawFloatManipulation(ref vector3.Y, fieldIndex, 1)
                            | DrawFloatManipulation(ref vector3.Z, fieldIndex, 2))
                        {
                            fi.SetValue(obj, vector3);
                            objModified = true;
                        }
                    }
                    else if (o is Vector4 vector4)
                    {
                        if (DrawFloatManipulation(ref vector4.X, fieldIndex, 0)
                            | DrawFloatManipulation(ref vector4.Y, fieldIndex, 1)
                            | DrawFloatManipulation(ref vector4.Z, fieldIndex, 2)
                            | DrawFloatManipulation(ref vector4.W, fieldIndex, 3))
                        {
                            fi.SetValue(obj, vector4);
                            objModified = true;
                        }
                    }
                    else if (o is Quaternion q)
                    {
                        if (DrawFloatManipulation(ref q.X, fieldIndex, 0)
                            | DrawFloatManipulation(ref q.Y, fieldIndex, 1)
                            | DrawFloatManipulation(ref q.Z, fieldIndex, 2)
                            | DrawFloatManipulation(ref q.W, fieldIndex, 3))
                        {
                            fi.SetValue(obj, q);
                            objModified = true;
                        }
                    }

                    else
                    {
                        ImGui.SetNextItemWidth(valueColumnWidth);
                        ImGui.TextUnformatted("?");
                        ImGui.SameLine();
                    }
                }

                if (objModified)
                {
                    list[objectIndex] = obj;
                    listModified = true;
                }

                if (ImGui.Button("+"))
                {
                    list.Insert(objectIndex, obj);
                }

                ImGui.SameLine();

                if (ImGui.Button("-"))
                {
                    list.Remove(objectIndex);
                }

                ImGui.PopID();
            }
        }
        ImGui.PopFont();
        ImGui.PopStyleVar(); // FramePadding
        ImGui.EndChild();
            
        return listModified;

        bool DrawFloatManipulation(ref float f, int fieldIndex, int componentIndex = 0)
        {
            ImGui.PushID(fieldIndex * 1000 + componentIndex);
            ImGui.SetNextItemWidth(valueColumnWidth);
            var grayedOut = (Math.Abs(f) < 0.0001f);
            if (grayedOut)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, UiColors.BackgroundFull.Rgba);
            }

            var fieldModified = ImGui.DragFloat("##sdf", ref f, 0.01f);

            if (grayedOut)
            {
                ImGui.PopStyleColor();
            }
            ImGui.SameLine();
            ImGui.PopID();
            return fieldModified;
        }
    }
}