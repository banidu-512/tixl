import json, re, uuid

SRC = r"C:\Users\Artonurban\Documents\TiXL4.4-alpha\Test\Symbols\Test.t3"
DST = r"C:\Users\Artonurban\Documents\TiXL4.4-alpha\RoomScan3D\Symbols"
HOME = "84b29bcb-f5d3-4313-ae4a-1978c51ba380"
NS = uuid.UUID("c2e7b2ef-7e3b-4b65-8a6f-3d0b8f4c2a11")
PROJECT = "Artonurban.RoomScan3D"
ADDRESS = "10.0.0.111"

test = json.loads(re.sub(r"/\*.*?\*/", "", open(SRC, encoding='utf8').read()))

PICK = {
    "abd74979-38e3-4bab-9d61-26acc6c65dbf": ("OnvifCamera", (-1500.0, 0.0)),
    "a1924dd8-db22-47ec-9870-eee178c8b65a": ("AxisPtzCamera", (-1500.0, 300.0)),
    "765eb3fe-47e9-481f-b685-335af68a55be": ("PtzScanPath", (-1100.0, 150.0)),
    "1006f5dd-eaa8-411a-88db-1589e7432fe1": ("DepthAnything", (-700.0, 0.0)),
    "3d5ca7f1-8b24-4c6a-9e35-2f1d0a9b7c44": ("DepthScan3D", (-300.0, 100.0)),
    "7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93": ("DrawPoints", (100.0, 100.0)),
}
new_id = {old: str(uuid.uuid5(NS, old)) for old in PICK}

DROP = ("74185692", "2f67a37f", "3b78b48b", "8451268e", "c306a59f", "d417b6a0", "c417b6a0")  # machine-specific or set below
OVERRIDE = {
    "1e56f26e": ADDRESS,                                   # OnvifCamera.Address
    "e3a506c8": ADDRESS,                                   # AxisPtzCamera.Address
    "34567890": 13,                                        # DepthAnything.ModelSizeParam: V3 Base (6-view stream)
    "75c48797": PROJECT + ":DepthScans",                   # DepthScan3D.SaveFolder
    "0773ed04": "room_scan",                               # DepthScan3D.SaveFileName
}

children = []
for c in test["Children"]:
    if c["Id"] not in PICK:
        continue
    name = PICK[c["Id"]][0]
    values = []
    seen = set()
    for v in c.get("InputValues", []):
        key = v["Id"][:8]
        if key in DROP:
            continue
        if key in OVERRIDE:
            v = dict(v)
            v["Value"] = OVERRIDE[key]
        values.append(v)
        seen.add(key)

    def add(idfull, typ, value):
        values.append({"Id": idfull, "Type": typ, "Value": value})

    if name == "OnvifCamera":
        add("1e56f26e-6e78-4f2e-cf56-7f8a8b9c0d2f", "System.String", ADDRESS) if "1e56f26e" not in seen else None
        add("4c89c59c-9c01-625c-fc89-0c1d2e3f4c5c", "System.Boolean", True) if "4c89c59c" not in seen else None
    if name == "AxisPtzCamera" and "e3a506c8" not in seen:
        add("e3a506c8-96fb-44d2-a8f7-c1045d96bf8e", "System.String", ADDRESS)
    if name == "PtzScanPath":
        add("c306a59f-e21b-478d-94a9-5f6b2d3c7e78", "System.Boolean", True)          # Run
        add("c417b6a0-f32c-489e-a5ba-607c3e4d8f9a", "System.Boolean", False)         # Continuous off: stop and capture
    if name == "DepthScan3D":
        if "75c48797" not in seen:
            add("75c48797-" + "0000-0000-0000-000000000000", "System.String", PROJECT + ":DepthScans")  # placeholder, fixed below
    children.append({"Id": new_id[c["Id"]], "SymbolId": c["SymbolId"], "SymbolName": c["SymbolName"], "InputValues": values, "Outputs": []})

# resolve the real SaveFolder / SaveFileName input ids from the DepthScan3D symbol definition
scan_def = json.loads(re.sub(r"/\*(.*?)\*/", lambda m: "", open(
    r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.t3", encoding='utf8').read()))
names = {}
raw_def = open(r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.t3", encoding='utf8').read()
for m in re.finditer(r'"Id": "([0-9a-f-]{36})"/\*(\w+)\*/', raw_def):
    names[m.group(2)] = m.group(1)
scan = next(c for c in children if c["SymbolName"].endswith("DepthScan3D"))
scan["InputValues"] = [v for v in scan["InputValues"] if not v["Id"].startswith("75c48797-0000")]
for inp, typ, val in [("SaveFolder", "System.String", PROJECT + ":DepthScans"), ("SaveFileName", "System.String", "room_scan"),
                      ("SaveFormat", "System.Int32", 2), ("PixelStride", "System.Int32", 1)]:
    fid = names[inp]
    scan["InputValues"] = [v for v in scan["InputValues"] if v["Id"] != fid]
    scan["InputValues"].append({"Id": fid, "Type": typ, "Value": val})

conns = []
for k in test["Connections"]:
    s, t = k["SourceParentOrChildId"], k["TargetParentOrChildId"]
    if s in PICK and t in PICK:
        conns.append({"SourceParentOrChildId": new_id[s], "SourceSlotId": k["SourceSlotId"],
                      "TargetParentOrChildId": new_id[t], "TargetSlotId": k["TargetSlotId"]})

sym = {"FormatVersion": 3, "Id": HOME, "Inputs": [], "Children": children, "Connections": conns}
open(DST + r"\RoomScan3D.t3", "w", encoding="utf8", newline="\r\n").write(json.dumps(sym, indent=2) + "\n")

ui = {"FormatVersion": 3, "Id": HOME,
      "Description": "Room scan with a PTZ camera: OnvifCamera moves the camera, AxisPtzCamera reads its pan/tilt, PtzScanPath drives a stop-and-capture sweep, DepthAnything (V3 Base 6-view stream) estimates depth, DepthScan3D merges every capture into one point cloud and DrawPoints shows it. Select DrawPoints to watch the cloud fill in.",
      "InputUis": [],
      "SymbolChildUis": [{"ChildId": new_id[o], "SymbolName": next(c["SymbolName"] for c in children if c["Id"] == new_id[o]),
                          "Position": {"X": PICK[o][1][0], "Y": PICK[o][1][1]}} for o in PICK],
      "OutputUis": [], "Sections": []}
open(DST + r"\RoomScan3D.t3ui", "w", encoding="utf8", newline="\r\n").write(json.dumps(ui, indent=2) + "\n")
print(len(children), "children", len(conns), "connections")
for c in children:
    print(" ", c["SymbolName"].split(".")[-1], [(v["Id"][:8], v["Value"]) for v in c["InputValues"]])
