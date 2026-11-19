import socket, json, time, sys

HOST_PORT, JOIN_PORT, RELAY = 9142, 9143, "127.0.0.1:45991"

class Bridge:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=30)
        self.f = self.s.makefile("rwb")
        self.n = 0

    def call(self, method, **kw):
        self.n += 1
        self.f.write((json.dumps({"id": str(self.n), "method": method, **kw}) + "\n").encode())
        self.f.flush()
        r = json.loads(self.f.readline())
        if not r.get("ok"):
            raise RuntimeError(f"{method} failed: {r}")
        return r.get("result", {})

    def collab(self, op, **kw):
        return self.call("collab", op=op, **kw)

def connect(port):
    for _ in range(90):
        try:
            return Bridge(port)
        except OSError:
            time.sleep(1)
    raise SystemExit(f"no editor on {port}")

host, join = connect(HOST_PORT), connect(JOIN_PORT)
host.call("ping"); join.call("ping")
print("both editors up")

st = host.collab("host-relay", relay=RELAY)
for _ in range(30):
    st = host.collab("status")
    if st["room"]:
        break
    time.sleep(0.5)
room = st["room"]
print("host:", st["state"], "room", room, st["status"], st["error"])

join.collab("join-relay", relay=RELAY, room=room)
for _ in range(40):
    js, hs = join.collab("status"), host.collab("status")
    if js["state"] == "Joined" or hs["peers"] > 0:
        break
    time.sleep(0.5)
print("join:", js["state"], js["status"], js["error"], "| host peers:", hs["peers"])

base_host, base_join = hs["chatLines"], js["chatLines"]
sent_join = sent_host = 0
for n in range(1, 300):
    join.collab("chat", text="j" * n); sent_join += 1
    host.collab("chat", text="h" * n); sent_host += 1
    time.sleep(0.12)
time.sleep(3)
hs, js = host.collab("status"), join.collab("status")
print(f"host received {hs['chatLines'] - base_host} of {sent_join} (+{sent_host} own lines)")
print(f"join received {js['chatLines'] - base_join} of {sent_host} (+{sent_join} own lines)")
print("final states:", hs["state"], hs["peers"], "|", js["state"], js["error"])
