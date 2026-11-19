import re,math,glob,os
log=max(glob.glob(r"C:\Users\Artonurban\AppData\Roaming\TiXL4.4-alpha\Log\*.log"),key=os.path.getmtime)
R=re.compile(r"(\d\d:\d\d:\d\d)\.\d+.*pos=<([^>]+)> target=<([^>]+)> up=<([^>]+)>.*source=(\S+)")
v=lambda s:tuple(map(float,s.split(',')))
S=[(m.group(1),v(m.group(2)),v(m.group(3)),m.group(5)) for l in open(log,encoding='utf8',errors='replace') for m in [R.search(l)] if m]
marks=[l.split() for l in open(r"D:\CrossTixlClaude\deploy-out\shots\stream_marks.txt")]
print(len(S),"pose samples, source",S[-1][3] if S else None)
out=[]
for i,t,*_ in marks:
    cand=[s for s in S if s[0]<=t]
    if not cand: continue
    s=cand[-1];f=[s[2][k]-s[1][k] for k in range(3)];az=math.degrees(math.atan2(f[0],-f[2]));pitch=math.degrees(math.atan2(f[1],math.hypot(f[0],f[2])))
    out.append((int(i),az,pitch,s[1]))
for k,(i,az,p,pos) in enumerate(out):
    print(i,"commanded pan %d"%(170+8*i),"yaw %6.1f"%az,"step %5.1f"%(az-out[k-1][1] if k else 0),"pitch %5.1f"%p,"pos (%.2f,%.2f,%.2f)"%pos)
