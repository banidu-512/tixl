import socket,json,array,sys
s=socket.create_connection(("127.0.0.1",9042),timeout=900);f=s.makefile("rwb")
f.write(b'{"id":"1","method":"getOutput","childId":"0045480b-06c7-550e-ab82-6db21d5a6083","outputName":"Points","update":true,"readCount":1000000}\n');f.flush()
el=json.loads(f.readline())["result"]["value"]["elements"]
a=array.array('f')
for e in el: a.extend(e["position"]+e["color"][:3])
open(sys.argv[1],"wb").write(a.tobytes());print("saved",len(el))
