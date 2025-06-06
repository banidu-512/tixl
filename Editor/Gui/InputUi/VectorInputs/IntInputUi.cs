﻿using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Utils;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.InputUi.VectorInputs;

internal sealed class IntInputUi : IntVectorInputValueUi<int>
{
    public override bool IsAnimatable => true;

    public IntInputUi() : base(1) { }
        
    public override IInputUi Clone()
    {
        return new IntInputUi()
                   {
                       InputDefinition = InputDefinition,
                       Parent = Parent,
                       PosOnCanvas = PosOnCanvas,
                       Relevancy = Relevancy
                   };
    }
        
    protected override InputEditStateFlags DrawEditControl(string name, Symbol.Child.Input input, ref int value, bool readOnly)
    {
        InputEditStateFlags result;
            
        if (MappedType != null)
        {
            result = DrawEnumInputEdit(ref value, MappedType);
        }
        else
        {
            IntComponents[0] = value;
            result = VectorValueEdit.Draw(IntComponents, Min, Max, Scale, Clamp);
            if (readOnly)
                return InputEditStateFlags.Nothing;
                
            value = IntComponents[0];
        }
            
        return result;            
    }

    public InputEditStateFlags DrawEditControl(ref int value)
    {
        return SingleValueEdit.Draw(ref value, -Vector2.UnitX, Min, Max, Clamp, Scale);
    }

    public override void ApplyValueToAnimation(IInputSlot inputSlot, InputValue inputValue, Animator animator, double time)
    {
        if (inputValue is not InputValue<int> typedInputValue)
            return;
            
        var curves = animator.GetCurvesForInput(inputSlot).ToArray();
        IntComponents[0] = typedInputValue.Value;
        Curve.UpdateCurveValues(curves, time, IntComponents);
    }    
        
    public static InputEditStateFlags DrawEnumInputEdit(ref int value, Type enumType)
    {
        var enumInfo = EnumCache.Instance.GetEnumEntry(enumType);

        if (enumInfo.IsFlagEnum)
        {
            // show as checkboxes
            InputEditStateFlags editStateFlags = InputEditStateFlags.Nothing;
            if (ImGui.TreeNode("##enumParamInt124"))
            {
                bool[] checks = enumInfo.SetFlags;
                for (int i = 0; i < enumInfo.ValueNames.Length; i++)
                {
                    int enumValueAsInt = enumInfo.ValuesAsInt[i];
                    checks[i] = (value & enumValueAsInt) > 0;
                    if (ImGui.Checkbox(enumInfo.ValueNames[i], ref checks[i]))
                    {
                        // value modified, store new flag
                        if (checks[i])
                        {
                            value |= enumValueAsInt;
                        }
                        else
                        {
                            value &= ~enumValueAsInt;
                        }

                        editStateFlags |= InputEditStateFlags.Modified;
                    }

                    if (ImGui.IsItemClicked())
                    {
                        editStateFlags |= InputEditStateFlags.Started;
                    }

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        editStateFlags |= InputEditStateFlags.Finished;
                    }
                }

                ImGui.TreePop();
            }

            return editStateFlags;
        }
        else
        {
            int index = Array.IndexOf(enumInfo.ValuesAsInt, value);
            InputEditStateFlags editStateFlags = InputEditStateFlags.Nothing;
            bool modified = ImGui.Combo("##dropDownParam", ref index, enumInfo.ValueNames, enumInfo.ValueNames.Length, 20);
            if (modified)
            {
                value = enumInfo.ValuesAsInt[index];
                editStateFlags |= InputEditStateFlags.ModifiedAndFinished;
            }

            if (!ImGui.IsItemActive())
            {
                var io = ImGui.GetIO();
                if (ImGui.IsItemHovered() && io.KeyCtrl)
                {
                    T3Ui.MouseWheelFieldHovered = true;
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                    var dl = ImGui.GetForegroundDrawList();
                    dl.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), UiColors.StatusActivated);

                    var wheel = io.MouseWheel;
                    if (wheel == 0)
                        return InputEditStateFlags.Nothing;

                    var delta = wheel > 0 ? -1 : 1;
                    value= (value + delta).Clamp(0, enumInfo.ValueNames.Length-1);
                    
                    return InputEditStateFlags.Modified;
                }
            }
            else if (ImGui.IsItemClicked())
            {
                editStateFlags |= InputEditStateFlags.Started;
            }

            return editStateFlags;
        }
    }
}