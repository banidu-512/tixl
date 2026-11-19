import json, re, uuid

SRC = r"C:\Users\Artonurban\Documents\TiXL4.4-alpha\Test\Symbols\Test.t3"
OUT = r"D:\CrossTixlClaude\Operators\Examples\Symbols\Lib\io\video\depthanything"
NS = uuid.UUID("b1d6a1de-6d2a-4a54-9f5e-2c9a7e3b1f00")

raw = open(SRC, encoding='utf8').read()
test = json.loads(re.sub(r"/\*.*?\*/", "", raw))

PICK = {
    "abd74979-38e3-4bab-9d61-26acc6c65dbf": ("OnvifCamera", (-1500.0, 0.0)),
    "a1924dd8-db22-47ec-9870-eee178c8b65a": ("AxisPtzCamera", (-1500.0, 300.0)),
    "765eb3fe-47e9-481f-b685-335af68a55be": ("PtzScanPath", (-1100.0, 150.0)),
    "1006f5dd-eaa8-411a-88db-1589e7432fe1": ("DepthAnything", (-700.0, 0.0)),
    "3d5ca7f1-8b24-4c6a-9e35-2f1d0a9b7c44": ("DepthScan3D", (-300.0, 100.0)),
    "7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93": ("DrawPoints", (100.0, 100.0)),
}
new_id = {old: str(uuid.uuid5(NS, old)) for old in PICK}

DROP_INPUT_PREFIXES = (
    "74185692",  # OnvifCamera.LocalIpAddress (machine specific)
    "2f67a37f",  # OnvifCamera.Username
    "3b78b48b",  # OnvifCamera.Password
    "75c48797",  # DepthScan3D.SaveFolder (project address)
    "8451268e",  # OnvifCamera.Discover (needs the local IP)
)
ADDR_ONVIF = "1e56f26e-6e78-4f2e-cf56-7f8a8b9c0d2f"
ADDR_AXIS = "e3a506c8-96fb-44d2-a8f7-c1045d96bf8e"
RUN_PTZ = "c306a59f-e21b-478d-94a9-5f6b2d3c7e78"
DRAW_OUT = "b73347d9-9d9f-4929-b9df-e2d6db722856"

SYMBOL_ID = "5d2f8c14-7a39-4e6b-b1c0-9e4a2d7f3b58"
IN_ADDRESS = "6e30a925-8b4a-4f7c-c2d1-af5b3e804c69"
IN_RUN = "7f41ba36-9c5b-408d-d3e2-b06c4f915d7a"
OUT_CLOUD = "8052cb47-ad6c-419e-e4f3-c17d50a26e8b"

children = []
for c in test["Children"]:
    if c["Id"] not in PICK:
        continue
    values = []
    for v in c.get("InputValues", []):
        if v["Id"].startswith(DROP_INPUT_PREFIXES) or v["Id"] in (ADDR_ONVIF, ADDR_AXIS):
            continue
        values.append(v)
    entry = {"Id": new_id[c["Id"]], "SymbolId": c["SymbolId"], "SymbolName": c["SymbolName"], "InputValues": values, "Outputs": []}
    # scan runs on its own, stop and capture, no stale restart pulse
    if PICK[c["Id"]][0] == "PtzScanPath":
        entry["InputValues"] = [v for v in values if not v["Id"].startswith(("c306a59f", "d417b6a0", "c417b6a0"))]
        entry["InputValues"].append({"Id": "c417b6a0-f32c-489e-a5ba-607c3e4d8f9a", "Type": "System.Boolean", "Value": False})
    children.append(entry)

conns = []
for k in test["Connections"]:
    s, t = k["SourceParentOrChildId"], k["TargetParentOrChildId"]
    if s in PICK and t in PICK:
        conns.append({"SourceParentOrChildId": new_id[s], "SourceSlotId": k["SourceSlotId"],
                      "TargetParentOrChildId": new_id[t], "TargetSlotId": k["TargetSlotId"]})
ZERO = "00000000-0000-0000-0000-000000000000"
conns += [
    {"SourceParentOrChildId": ZERO, "SourceSlotId": IN_ADDRESS, "TargetParentOrChildId": new_id["abd74979-38e3-4bab-9d61-26acc6c65dbf"], "TargetSlotId": ADDR_ONVIF},
    {"SourceParentOrChildId": ZERO, "SourceSlotId": IN_ADDRESS, "TargetParentOrChildId": new_id["a1924dd8-db22-47ec-9870-eee178c8b65a"], "TargetSlotId": ADDR_AXIS},
    {"SourceParentOrChildId": ZERO, "SourceSlotId": IN_RUN, "TargetParentOrChildId": new_id["765eb3fe-47e9-481f-b685-335af68a55be"], "TargetSlotId": RUN_PTZ},
    {"SourceParentOrChildId": new_id["7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93"], "SourceSlotId": DRAW_OUT, "TargetParentOrChildId": ZERO, "TargetSlotId": OUT_CLOUD},
]
print(len(children), "children", len(conns), "connections")
for c in conns:
    print(" ", c["SourceParentOrChildId"][:8], c["SourceSlotId"][:8], "->", c["TargetParentOrChildId"][:8], c["TargetSlotId"][:8])

sym = {"FormatVersion": 3, "Id": SYMBOL_ID,
       "Inputs": [{"Id": IN_ADDRESS, "DefaultValue": "10.0.0.111"}, {"Id": IN_RUN, "DefaultValue": True}],
       "Children": children, "Connections": conns}
open(OUT + r"\PtzRoomScanExample.t3", "w", encoding="utf8", newline="\r\n").write(json.dumps(sym, indent=2) + "\n")

ui = {"FormatVersion": 3, "Id": SYMBOL_ID,
      "Description": "Scans a whole room with a pan/tilt (PTZ) network camera and shows it as a point cloud. The camera stops at each raster position, DepthAnything estimates depth, and DepthScan3D merges every capture by the camera's own pan/tilt into one range map that snaps to a fitted room box. The output draws the cloud; it fills in as the sweep runs. Set Address to your AXIS camera and turn Run on. Save a closed mesh with DepthScan3D's SaveFormat 'PLY mesh (room box)'.",
      "SymbolTags": "10",
      "InputUis": [
          {"InputId": IN_ADDRESS, "Position": {"X": -1800.0, "Y": 100.0}, "Description": "IP address of the AXIS PTZ camera (ONVIF and VAPIX)."},
          {"InputId": IN_RUN, "Position": {"X": -1800.0, "Y": 220.0}, "Description": "Runs the scan; off pauses it at the current stop."}],
      "SymbolChildUis": [{"ChildId": new_id[o], "SymbolName": next(c["SymbolName"] for c in children if c["Id"] == new_id[o]),
                          "Position": {"X": PICK[o][1][0], "Y": PICK[o][1][1]}} for o in PICK],
      "OutputUis": [{"OutputId": OUT_CLOUD, "Position": {"X": 500.0, "Y": 100.0}}]}
open(OUT + r"\PtzRoomScanExample.t3ui", "w", encoding="utf8", newline="\r\n").write(json.dumps(ui, indent=2) + "\n")

cs = '''using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.Lib.io.video.depthanything;

[Guid("%s")]
internal sealed class PtzRoomScanExample : Instance<PtzRoomScanExample>
{
    [Output(Guid = "%s")]
    public readonly Slot<Command> Cloud = new();

    [Input(Guid = "%s")]
    public readonly InputSlot<string> Address = new("10.0.0.111");

    [Input(Guid = "%s")]
    public readonly InputSlot<bool> Run = new(true);
}
''' % (SYMBOL_ID, OUT_CLOUD, IN_ADDRESS, IN_RUN)
open(OUT + r"\PtzRoomScanExample.cs", "w", encoding="utf8", newline="\r\n").write(cs)
print("written")
