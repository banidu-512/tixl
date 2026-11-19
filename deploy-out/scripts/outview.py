import numpy as np, sys
from PIL import Image
a=np.fromfile(sys.argv[1],dtype=np.float32).reshape(-1,6)
P=a[:,:3].astype(np.float64);C=(a[:,3:]*255).clip(0,255).astype(np.uint8)
Wd,Hd=520,380;tiles=[]
c=P.mean(0)
for yaw,pitch in [(35,25),(215,25),(0,89)]:
    cy,sy=np.cos(np.radians(yaw)),np.sin(np.radians(yaw));cp,sp=np.cos(np.radians(pitch)),np.sin(np.radians(pitch))
    Ry=np.array([[cy,0,sy],[0,1,0],[-sy,0,cy]]);Rx=np.array([[1,0,0],[0,cp,-sp],[0,sp,cp]])
    Q=((P-c)@Ry)@Rx.T;dist=4.5;z=dist-Q[:,2];m=z>0.1
    f=Wd*1.1
    u=(Q[m,0]/z[m]*f+Wd/2).astype(int);w=(-Q[m,1]/z[m]*f+Hd/2).astype(int)
    ok=(u>=1)&(u<Wd-1)&(w>=1)&(w<Hd-1)
    idx=np.nonzero(m)[0][ok];uu=u[ok];ww=w[ok];zz=z[m][ok];o=np.argsort(-zz)
    img=np.full((Hd,Wd,3),25,np.uint8)
    for dx in (0,1):
        for dy in (0,1):
            img[ww[o]+dy,uu[o]+dx]=C[idx[o]]
    tiles.append(img)
Image.fromarray(np.hstack(tiles)).save(sys.argv[2])
