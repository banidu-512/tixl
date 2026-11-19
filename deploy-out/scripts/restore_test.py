import json, re, uuid

EX = r"D:\CrossTixlClaude\Operators\Examples\Symbols\Lib\io\video\depthanything\PtzRoomScanExample.t3"
T3 = r"C:\Users\Artonurban\Documents\TiXL4.4-alpha\Test\Symbols\Test.t3"
T3UI = r"C:\Users\Artonurban\Documents\TiXL4.4-alpha\Test\Symbols\Test.t3ui"
NS = uuid.UUID("b1d6a1de-6d2a-4a54-9f5e-2c9a7e3b1f00")
ADDRESS = "10.0.0.111"

PICK = {
    "abd74979-38e3-4bab-9d61-26acc6c65dbf": (-2526.0, -4347.4),   # OnvifCamera
    "a1924dd8-db22-47ec-9870-eee178c8b65a": (-3311.0, -3961.6),   # AxisPtzCamera
    "765eb3fe-47e9-481f-b685-335af68a55be": (-2900.0, -4200.0),   # PtzScanPath
    "1006f5dd-eaa8-411a-88db-1589e7432fe1": (-2338.1, -4139.7),   # DepthAnything
    "3d5ca7f1-8b24-4c6a-9e35-2f1d0a9b7c44": (-2250.0, -3900.0),   # DepthScan3D
    "7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93": (-2050.0, -3900.0),   # DrawPoints (world)
}
orig = {str(uuid.uuid5(NS, o)): o for o in PICK}

ex = json.loads(open(EX, encoding='utf8').read())
raw = open(T3, encoding='utf8').read()
t3 = json.loads(re.sub(r"/\*.*?\*/", "", raw))
have = {c["Id"] for c in t3["Children"]}
ZERO = "00000000-0000-0000-0000-000000000000"

added = []
for c in ex["Children"]:
    old = orig[c["Id"]]
    if old in have:
        continue
    c = dict(c)
    c["Id"] = old
    values = list(c["InputValues"])
    if c["SymbolName"].endswith("OnvifCamera"):
        values.append({"Id": "1e56f26e-6e78-4f2e-cf56-7f8a8b9c0d2f", "Type": "System.String", "Value": ADDRESS})
    if c["SymbolName"].endswith("AxisPtzCamera"):
        values.append({"Id": "e3a506c8-96fb-44d2-a8f7-c1045d96bf8e", "Type": "System.String", "Value": ADDRESS})
    if c["SymbolName"].endswith("PtzScanPath"):
        values.append({"Id": "c306a59f-e21b-478d-94a9-5f6b2d3c7e78", "Type": "System.Boolean", "Value": True})
    c["InputValues"] = values
    t3["Children"].append(c)
    added.append(c["SymbolName"].split(".")[-1])

have_conn = {(k["SourceParentOrChildId"], k["SourceSlotId"], k["TargetParentOrChildId"], k["TargetSlotId"]) for k in t3["Connections"]}
for k in ex["Connections"]:
    s, t = k["SourceParentOrChildId"], k["TargetParentOrChildId"]
    if s == ZERO or t == ZERO:
        continue
    key = (orig[s], k["SourceSlotId"], orig[t], k["TargetSlotId"])
    if key not in have_conn:
        t3["Connections"].append({"SourceParentOrChildId": key[0], "SourceSlotId": key[1], "TargetParentOrChildId": key[2], "TargetSlotId": key[3]})

# the camera pose trail the earlier tests used
if "7941e68c-5817-4acc-bd60-e839b39303f1" not in have:
    t3["Children"].append({"Id": "7941e68c-5817-4acc-bd60-e839b39303f1", "SymbolId": "d59ee7b2-8beb-470e-b75a-f73cc1916b1a",
                           "SymbolName": "Lib.render.camera.analyze.VisualizeCamTrail", "InputValues": [], "Outputs": []})
    t3["Connections"].append({"SourceParentOrChildId": "1006f5dd-eaa8-411a-88db-1589e7432fe1", "SourceSlotId": "5a3334ec-78c3-4001-8018-3d8aae7f870f",
                              "TargetParentOrChildId": "7941e68c-5817-4acc-bd60-e839b39303f1", "TargetSlotId": "7fd1c5f7-de89-46ed-b8ba-d14c2352848f"})
    added.append("VisualizeCamTrail")

open(T3, "w", encoding="utf8", newline="\r\n").write(json.dumps(t3, indent=2) + "\n")

ui = json.loads(re.sub(r"/\*.*?\*/", "", open(T3UI, encoding="utf8").read()))
placed = {u["ChildId"] for u in ui["SymbolChildUis"]}
for old, (x, y) in PICK.items():
    if old not in placed:
        ui["SymbolChildUis"].append({"ChildId": old, "Position": {"X": x, "Y": y}})
if "7941e68c-5817-4acc-bd60-e839b39303f1" not in placed:
    ui["SymbolChildUis"].append({"ChildId": "7941e68c-5817-4acc-bd60-e839b39303f1", "Position": {"X": -2093.7, "Y": -3672.6}})
open(T3UI, "w", encoding="utf8", newline="\r\n").write(json.dumps(ui, indent=2) + "\n")
print("restored:", added)
