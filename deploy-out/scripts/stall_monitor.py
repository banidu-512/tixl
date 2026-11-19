import socket, json, time, sys
dur=float(sys.argv[1]);worst=[];t0=time.time();n=0
s=socket.create_connection(("127.0.0.1",9042),timeout=30);f=s.makefile("rwb")
while time.time()-t0<dur:
    t=time.time();f.write(b'{"id":"1","method":"ping"}\n');f.flush();f.readline();dt=time.time()-t;n+=1
    if dt>0.4: worst.append((round(time.time()-t0,1),round(dt,2)))
    time.sleep(0.1)
print("pings",n,"stalls >0.4s:",len(worst),"longest",max([w[1] for w in worst],default=0),"s; at",worst[:12])
