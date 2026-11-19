import re,math,glob,os
from collections import Counter
log=max(glob.glob(r"C:\Users\Artonurban\AppData\Roaming\TiXL4.4-alpha\Log\*.log"),key=os.path.getmtime)
R=re.compile(r"(\d\d:\d\d:\d\d)\.\d+.*pos=<([^>]+)> target=<([^>]+)> up=<([^>]+)>.*source=(\S+)")
v=lambda s:tuple(map(float,s.split(',')))
S=[]
for l in open(log,encoding='utf8',errors='replace'):
    m=R.search(l)
    if m:
        p=v(m.group(2));t=v(m.group(3));f=[t[i]-p[i] for i in range(3)];n=math.sqrt(sum(x*x for x in f));f=[x/n for x in f]
        S.append((m.group(1),math.degrees(math.atan2(f[0],-f[2])),math.degrees(math.asin(max(-1,min(1,f[1])))),m.group(5)))
T=[]
for l in open(r"D:\CrossTixlClaude\deploy-out\shots\truth.txt"):
    m=re.match(r"(\d\d:\d\d:\d\d) pan=([-\d.]+) tilt=([-\d.]+)",l)
    if m: T.append((m.group(1),float(m.group(2)),float(m.group(3))))
print(len(S),"pose samples; sources:",Counter(s[3].split("(")[0] for s in S).most_common())
wrap=lambda a:(a+180)%360-180
at=lambda t:([s for s in S if s[0]<=t] or [None])[-1]
# first truth sample that has a tracker sample
k0=next(k for k,(t,_,_) in enumerate(T) if at(t))
t0=T[k0];s0=at(t0[0])
errs=[]
print("time      truth pan/tilt (rel) | tracker yaw/pitch (rel) | yaw err  pitch err | source")
for k in range(k0,len(T),5):
    t,pan,tilt=T[k];s=at(t)
    if not s: continue
    ty,tp=wrap(pan-t0[1]),tilt-t0[2];dy,dp=wrap(s[1]-s0[1]),s[2]-s0[2]
    ey=min(abs(wrap(dy-ty)),abs(wrap(dy+ty)));ep=min(abs(dp-tp),abs(dp+tp));errs.append((ey,ep))
    print("%s  %7.1f %6.1f | %7.1f %6.1f | %6.1f %6.1f | %s"%(t,ty,tp,dy,dp,ey,ep,s[3]))
import statistics
print("median abs error: yaw %.1f deg, pitch %.1f deg"%(statistics.median(e[0] for e in errs),statistics.median(e[1] for e in errs)))
