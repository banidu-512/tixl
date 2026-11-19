import socket, json, time

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

host, join = Bridge(9142), Bridge(9143)
st = host.collab("host-backend")
for _ in range(40):
    st = host.collab("status")
    if st["room"]:
        break
    time.sleep(0.5)
room = st["room"]
print("host:", st["state"], "room", room, "|", st["status"], "|", st["error"])

join.collab("join-backend", room=room)
approved = False
for i in range(120):
    hs, js = host.collab("status"), join.collab("status")
    if hs["pendingJoins"] > 0 and not approved:
        host.collab("join-respond", index=0, approve=True)
        approved = True
        print("host approved the join ticket")
    if hs["peers"] > 0 and js["state"] == "Connected":
        break
    time.sleep(0.5)
print("join:", js["state"], "|", js["status"], "|", js["error"])
print("host peers:", hs["peers"], "links:", hs["links"], "| join links:", js["links"], "| approved:", approved)

for n in (5, 123, 379, 50):
    join.collab("chat", text="j" * n)
    host.collab("chat", text="h" * n)
    time.sleep(0.3)
time.sleep(2)
hs, js = host.collab("status"), join.collab("status")
print("chat lines host/join:", hs["chatLines"], js["chatLines"], "| states:", hs["state"], js["state"])

join.collab("leave"); host.collab("leave")
time.sleep(2)
print("left:", host.collab("status")["state"], join.collab("status")["state"])
