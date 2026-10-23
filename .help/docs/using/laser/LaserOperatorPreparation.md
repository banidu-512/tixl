# Laser Operator Preparation Guide

This guide provides a checklist for preparing and adding laser operators to the TiXL repository. It covers the required file layout, UI conventions, GUID handling, and documentation steps.

---

## 1. Add Symbol Source Files

- Place the operator implementation **C#** file (`*.cs`), the definition file (`*.t3`), and the UI description file (`*.t3ui`) under `Operators/Lib/Symbols/laser/<category>/`.
- Accepted categories are `camera`, `input`, `output`, `optimize`, `safety`, and `convert`.
- Example:
  ```
  Operators/Lib/Symbols/laser/input/MyNewInput.cs
  Operators/Lib/Symbols/laser/input/MyNewInput.t3
  Operators/Lib/Symbols/laser/input/MyNewInput.t3ui
  ```

---

## 2. Update GUIDs

- Every operator class must have a unique `[Guid]` attribute. When adding a new operator, generate a fresh GUID for the class **and** for each input / output slot that declares a GUID.
- Generate fresh identifiers for every new operator to avoid duplicate GUIDs.

---

## 3. Flatten UI Layout

- The user prefers a *flat* UI (no `GroupTitle` headings). Remove any `GroupTitle` entries from `.t3ui` files.
- Ensure each input has a `Description` key – outputs ignore `Description` per the parser, but they are still useful for documentation.
- Refer to the **laser-operator-ui-flat-layout** memory for the exact change set.

---

## 4. Set Default Values Correctly

- `InputSlot<T>` does **not** have a `DefaultValue` property. Provide defaults via the `.t3` JSON `DefaultValue`, via constructor parameters, or guard logic in `Update`.
- See the **InputSlot no DefaultValue gotcha** for details.

---

## 5. Custom Dropdowns (when needed)

- For IP address fields or other enumerations, implement `ICustomDropdownHolder` in the C# class and add `"Usage": "CustomDropdown"` in the corresponding `.t3ui`.
- This mechanism is described in **TiXL custom dropdown mechanism**.

---

## 6. Documentation

- Add a new wiki page for the operator (or update an existing one) with the following sections:
  - Overview
  - Inputs & Outputs (including types and defaults)
  - Known quirks / limitations
  - Example usage
- Link this preparation guide from the operator's wiki page.

---

## 7. Verify Build & Tests

- Run `dotnet build` to ensure the solution compiles after adding the operator.
- Execute `dotnet test Core.Tests` to confirm that existing tests still pass.

---

## 8. Commit & Push

- Commit the changes with a clear message, e.g., `feat: add MyNewLaserOperator and update docs`.
- Push to the `feature/laser-operators` branch and create a PR targeting `main`.

---

## References

- laser-operators-uncrashable
- laser-operator-ui-flat-layout
- laser-ops-implementation-branch
- laser-core-etherdream-library-quirks
- ponk-output-operator-architecture
- ponk-input-operator-architecture
- tiXL-custom-dropdown-mechanism
