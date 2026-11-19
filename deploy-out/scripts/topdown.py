import numpy as np,sys
from PIL import Image
a=np.fromfile(sys.argv[1],dtype=np.float32).reshape(-1,6);p=a[:,:3];r=np.linalg.norm(p,axis=1);el=np.degrees(np.arcsin(p[:,1]/r))
m=(el>-5)&(el<40)&(r<8);x,z=p[m,0],p[m,2]
S=60;W=600;img=np.zeros((W,W,3),np.uint8)
ix=(x*S+W/2).astype(int);iy=(z*S+W/2).astype(int);k=(ix>=0)&(ix<W)&(iy>=0)&(iy<W)
img[iy[k],ix[k]]=(255,255,255);img[W//2-3:W//2+3,W//2-3:W//2+3]=(255,0,0)
for g in range(0,W,S): img[g,:,:]=np.maximum(img[g,:,:],40);img[:,g,:]=np.maximum(img[:,g,:],40)
Image.fromarray(img).save(sys.argv[2])
