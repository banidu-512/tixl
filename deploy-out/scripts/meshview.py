import numpy as np, sys
from PIL import Image
f=open(sys.argv[1],'rb'); hdr=b''
while not hdr.endswith(b'end_header\n'): hdr+=f.readline()
nv=int([l for l in hdr.decode().split('\n') if l.startswith('element vertex')][0].split()[-1])
v=np.frombuffer(f.read(nv*15),dtype=np.dtype([('p','<f4',3),('c','u1',3)]))
P=v['p'].astype(np.float64);C=v['c']
Wd,Hd=480,320;f_px=Wd/2/np.tan(np.radians(50)/1)  # ~100 deg hfov
tiles=[]
for yaw,pitch in [(0,0),(90,0),(180,0),(270,0),(0,70),(0,-60)]:
    cy,sy=np.cos(np.radians(yaw)),np.sin(np.radians(yaw));cp,sp=np.cos(np.radians(pitch)),np.sin(np.radians(pitch))
    # camera looks along -Z rotated by yaw about Y, then pitch about X
    Ry=np.array([[cy,0,sy],[0,1,0],[-sy,0,cy]]);Rx=np.array([[1,0,0],[0,cp,-sp],[0,sp,cp]])
    Q=(P@Ry)@Rx.T  # into camera frame (looking -Z)
    z=-Q[:,2];m=z>0.02
    u=(Q[m,0]/z[m]*f_px+Wd/2).astype(int);w=(-Q[m,1]/z[m]*f_px+Hd/2).astype(int)
    ok=(u>=0)&(u<Wd)&(w>=0)&(w<Hd)
    img=np.zeros((Hd,Wd,3),np.uint8);zb=np.full((Hd,Wd),1e9)
    idx=np.nonzero(m)[0][ok];uu=u[ok];ww=w[ok];zz=z[m][ok]
    order=np.argsort(-zz)
    for dx in (0,1):
        for dy in (0,1):
            img[np.clip(ww[order]+dy,0,Hd-1),np.clip(uu[order]+dx,0,Wd-1)]=C[idx[order]]
    tiles.append(img)
Image.fromarray(np.vstack([np.hstack(tiles[:3]),np.hstack(tiles[3:])])).save(sys.argv[2])
